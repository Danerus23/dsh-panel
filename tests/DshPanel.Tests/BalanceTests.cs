using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Agents;
using DshPanel.Balance;
using DshPanel.Isolation;
using DshPanel.Peak;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки баланса и окон пика.
///
/// Сети здесь НЕТ ни в одной проверке: ответы агента записаны заранее и разбираются как данные,
/// клиент подставной, файл ключей — свой временный. Настоящий ключ владельца и живой запрос
/// трогает только проба на собранном exe (`--balance-selftest --with-my-key`), и только потому,
/// что владелец разрешил это явно.
/// </summary>
public class BalanceTests
{
    /// <summary>Подставной клиент: сети не касается, ключ запоминает, чтобы проверять его отсутствие в текстах.</summary>
    private sealed class FakeClient : IBalanceClient
    {
        public BalanceResult? Next { get; set; }

        public int Calls { get; private set; }

        public List<string> Keys { get; } = new();

        /// <summary>
        /// Ответ по номеру запроса (нумерация с единицы). Нужен там, где важно, ЧЕЙ ответ показан:
        /// у прежнего агента и у нового разные суммы, и по одной строке видно, чей баланс на панели.
        /// </summary>
        public Func<int, BalanceResult>? Answer { get; set; }

        /// <summary>
        /// Номера запросов, ответы на которые клиент отдаёт НЕ ПО СВОЕЙ ВОЛЕ, а по команде проверки
        /// (<see cref="ReleaseAnswer"/>): такой запрос стоит ВНУТРИ клиента — ровно как настоящий,
        /// ждущий сеть. Без этой двери проверить смену агента нечем: с мгновенным подставным
        /// клиентом запрос успевает завершиться раньше, чем проверка дойдёт до своих утверждений
        /// (на этом она и падала на раннере 36638902335 — то на одном утверждении, то на другом).
        /// Ответы уходят по одному и в порядке запросов.
        /// </summary>
        public HashSet<int> HoldCalls { get; } = new();

        private readonly SemaphoreSlim _answers = new(0);

        /// <summary>Отпустить ровно один удержанный ответ.</summary>
        public void ReleaseAnswer() => _answers.Release();

        public BalanceResult Query(AgentProfile agent, string key, DateTimeOffset now)
        {
            Calls++;
            Keys.Add(key);

            if (HoldCalls.Contains(Calls)) _answers.Wait(TimeSpan.FromSeconds(10));

            return Next
                ?? Answer?.Invoke(Calls)
                ?? new BalanceResult(
                    true, true, 12.34m, "CNY", "12.34 CNY", "пополнено 12.34", string.Empty, now, agent.Id);
        }
    }

    /// <summary>Подставное состояние баланса для окна: строки готовые, решений нет.</summary>
    private sealed class FakeBalance : IBalanceControl
    {
        public AgentProfile Agent => AgentCatalog.Find(AgentId);

        /// <summary>Какого агента показывает контроллер. Меняется владельцем настроек, не окном.</summary>
        public string AgentId { get; set; } = AgentCatalog.DefaultId;

        public BalanceResult Result { get; set; } = BalanceResult.NotRequested(AgentCatalog.DefaultId);
        public PeakState Peak { get; set; } = default;
        public bool Busy => false;
        public bool Allowed => true;
        public TrayTone Tone { get; set; } = TrayTone.Good;
        public int Refreshes { get; private set; }

        /// <summary>Сколько раз панель сказала контроллеру «агент сменился».</summary>
        public int AgentChanges { get; private set; }

        public void Refresh() => Refreshes++;

        public void AgentChanged()
        {
            AgentChanges++;
            Changed?.Invoke();
        }

        public void Tick(DateTimeOffset now) { }
        public event Action? Changed;
        public string StatusText => "12.34 CNY";
        public string DetailText => "пополнено 12.34";
        public string PeakText => PanelStrings.PeakOffPeak;
        public string NextText => "Пик начнётся через 2 ч 0 мин (13:00)";
        public string PeakCheckedText => "Пики: проверено 24.09.2026";
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-balance-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Настройки в памяти: окну настроек нужен контроллер, а файл и тему трогать незачем.</summary>
    private sealed class FakeSettings : ISettingsControl
    {
        public FakeSettings(PanelSettings settings) => Settings = settings;

        public PanelSettings Settings { get; private set; }

        /// <summary>Можно ли менять настройки в этом наборе: прогон проверки их не меняет.</summary>
        public bool Writable { get; init; } = true;

        public string LoadProblem => string.Empty;

        public string WorkDirInUse => @"C:\Панель\data";

        public PanelSettings? SavedSettings { get; private set; }

        public IReadOnlyList<WorkDirSuggestion> Suggestions() => Array.Empty<WorkDirSuggestion>();

        public bool Save(PanelSettings settings)
        {
            // Заглушка ведёт себя как настоящий контроллер: без права писать — отказ, а с правом
            // записанное становится ДЕЙСТВУЮЩИМИ настройками. Это важно там, где проверяется
            // смена агента: окно читает именно действующие настройки.
            if (!Writable) return false;

            Settings = SettingsStore.Clean(settings);
            SavedSettings = Settings;
            return true;
        }

        public void PreviewTheme(string theme) { }

        /// <summary>
        /// Отчёт окружения у подмены ПУСТ: путей и версий она не знает. Так проверка видит ровно
        /// то, что нужно, — что окно не выдумывает находок на пустом отчёте.
        /// </summary>
        public EnvironmentReport EnvironmentInfo() => new(Array.Empty<EnvironmentGroup>());

        public string EnvironmentText() => "движок: найден";

        /// <summary>
        /// Заглушка этого набора настройки только ЧИТАЕТ: <see cref="Settings"/> здесь неизменяем,
        /// и менять состав копии через неё нечем. Честный отказ вместо тихой подмены.
        /// </summary>
        public bool SetBackupWithKeys(bool value) => false;

        public bool SetBackupKeyDirs(IReadOnlyList<string> directories) => false;


        /// <summary>И согласие на найденный сервер здесь тоже не запоминается — набор про баланс.</summary>
        public bool SetAdoptedServer(int port) => false;

        /// <summary>
        /// Смена активного агента здесь ЗАПИСЫВАЕТСЯ по-настоящему (копия настроек, правка,
        /// сохранение): именно этим путём главное окно меняет агента, и проверка обязана видеть
        /// настоящую запись, а не «окно сказало, что записало».
        /// </summary>
        public bool SetActiveAgent(string agentId)
        {
            var next = SettingsStore.Clean(Settings);
            next.ActiveAgent = agentId ?? string.Empty;
            return Save(next);
        }

        /// <summary>Размер и положение главного окна — тоже не дело этого набора: честный отказ.</summary>
        public bool SetWindowPlacement(int x, int y, int width, int height) => false;
    }

    private static void RemoveTemp(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
        }
        catch
        {
            // Родитель занят другой проверкой — не ошибка.
        }
    }

    private static BalanceController Controller(
        FakeClient client,
        PanelSettings settings,
        string credentialsPath,
        Action<NoticeKind, string, string> notify,
        bool allowed = true,
        DateTimeOffset? now = null)
    {
        var clock = now ?? new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

        return new BalanceController(
            client,
            credentialsPath,
            () => settings,
            allowed,
            () => clock,
            action => action(),
            _ => { },
            notify);
    }

    // ---------------------------------------------------------------- ключ

    /// <summary>
    /// Ключ ищется по имени в ОБЕИХ раскладках файла движка: плоской и версии 1. Значение
    /// не должно попадать ни в объяснение, ни в текст ошибки — иначе оно утечёт в журнал.
    /// </summary>
    [Fact]
    public void Ключ_читается_из_обеих_раскладок_и_значение_не_утекает()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".credentials.yaml");
            var key = "sk-" + new string('z', 30);

            File.WriteAllText(path, $"version: 1\nrefs:\n  DEEPSEEK_API_KEY: {key}\n");
            var version1 = CredentialsKey.Read(path, "DEEPSEEK_API_KEY");
            Assert.True(version1.Found);
            Assert.Equal(key, version1.Value);
            Assert.DoesNotContain(key, version1.Problem, StringComparison.Ordinal);

            File.WriteAllText(path, "DEEPSEEK_API_KEY: \"sk-flat-value-1234567890\"\n");
            Assert.Equal("sk-flat-value-1234567890", CredentialsKey.Read(path, "DEEPSEEK_API_KEY").Value);

            File.WriteAllText(path, "refs:\n  OTHER_KEY: value\n");
            var missing = CredentialsKey.Read(path, "DEEPSEEK_API_KEY");
            Assert.False(missing.Found);
            Assert.Contains("DEEPSEEK_API_KEY", missing.Problem, StringComparison.Ordinal);

            var absent = CredentialsKey.Read(Path.Combine(dir, "нет.yaml"), "DEEPSEEK_API_KEY");
            Assert.False(absent.Found);
            Assert.NotEqual(string.Empty, absent.Problem);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- разбор ответа

    [Fact]
    public void Ответ_баланса_разбирается_а_ключ_вырезается_из_ошибок()
    {
        const string key = "sk-secret-value-1234567890";

        using var document = JsonDocument.Parse("""
        {"is_available":true,"balance_infos":[
          {"currency":"CNY","total_balance":"12.34","granted_balance":"0.00","topped_up_balance":"12.34"},
          {"currency":"USD","total_balance":"1.00","topped_up_balance":"1.00"}]}
        """);

        var parsed = HttpBalanceClient.Parse(document.RootElement, AgentCatalog.DefaultId, DateTimeOffset.Now);

        Assert.True(parsed.Ok);
        Assert.True(parsed.Available);
        Assert.Equal(12.34m, parsed.Total);
        Assert.Equal("CNY", parsed.Currency);
        Assert.Contains("12.34 CNY", parsed.Summary, StringComparison.Ordinal);
        Assert.Contains("1.00 USD", parsed.Summary, StringComparison.Ordinal);
        Assert.Contains("пополнено", parsed.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(key, HttpBalanceClient.Redact($"Authorization: Bearer {key}", key), StringComparison.Ordinal);
        Assert.Contains("***", HttpBalanceClient.Redact($"Authorization: Bearer {key}", key), StringComparison.Ordinal);
        Assert.Equal("обычный текст", HttpBalanceClient.Redact("обычный текст", key));
    }

    [Fact]
    public void Пустой_ответ_и_недоступный_счёт_не_ломают_разбор()
    {
        using (var empty = JsonDocument.Parse("{}"))
        {
            var parsed = HttpBalanceClient.Parse(empty.RootElement, AgentCatalog.DefaultId, DateTimeOffset.Now);
            Assert.True(parsed.Ok);
            Assert.Null(parsed.Total);
            Assert.Equal(PanelStrings.BalanceNoData, parsed.Summary);
        }

        using (var unavailable = JsonDocument.Parse("""{"is_available":false,"balance_infos":[]}"""))
        {
            var parsed = HttpBalanceClient.Parse(unavailable.RootElement, AgentCatalog.DefaultId, DateTimeOffset.Now);
            Assert.True(parsed.Ok);
            Assert.False(parsed.Available);
            Assert.Null(parsed.Total);
        }
    }

    // ---------------------------------------------------------------- решения

    [Fact]
    public void Решения_о_предупреждении_перебирают_все_случаи()
    {
        // Порог: булево «включено», «ниже порога», «уже предупреждали».
        var low = new (bool Enabled, decimal? Total, bool Warned, bool Expected)[]
        {
            (true, 5m, false, true),
            (true, 5m, true, false),
            (true, 10m, false, false),
            (true, null, false, false),
            (false, 5m, false, false),
        };

        Assert.Equal(5, low.Length);
        foreach (var c in low)
            Assert.Equal(c.Expected, BalanceDecisions.ShouldWarnLow(c.Enabled, c.Total, 10m, c.Warned));

        // Пик: время предупреждения, «в пике ли», вид переключения, минут до него, предупреждали ли.
        var start = new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Utc);
        var peak = new (int Lead, bool InPeak, PeakMoment Kind, int Minutes, DateTime? WarnedFor, bool Expected)[]
        {
            (30, false, PeakMoment.PeakStart, 15, null, true),
            (30, false, PeakMoment.PeakStart, 15, start, false),
            (30, true, PeakMoment.PeakStart, 15, null, false),
            (10, false, PeakMoment.PeakStart, 15, null, false),
            (0, false, PeakMoment.PeakStart, 15, null, false),
            (30, false, PeakMoment.PeakEnd, 15, null, false),
            (30, false, PeakMoment.PeakStart, 0, start, false),
        };

        Assert.Equal(7, peak.Length);
        foreach (var c in peak)
        {
            Assert.Equal(
                c.Expected,
                BalanceDecisions.ShouldWarnPeak(c.Lead, c.InPeak, c.Kind, c.Minutes, start, c.WarnedFor));
        }
    }

    // ---------------------------------------------------------------- окна пика

    /// <summary>
    /// Таблица ожиданий записана ЯВНО: формула повторяла бы код и не заметила бы, что её поменяли.
    /// Время — UTC, как на официальной странице цен (проверено 24.09.2026).
    /// </summary>
    [Fact]
    public void Окна_пика_считают_пик_и_следующее_переключение()
    {
        var agent = AgentCatalog.DeepSeek;
        var at = new (DateTimeOffset Moment, bool InPeak, PeakMoment Kind, int Minutes)[]
        {
            // пн 02:00 UTC — внутри окна 01:00–04:00, конец через 2 часа
            (new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero), true, PeakMoment.PeakEnd, 120),
            // пн 00:30 UTC — до начала пика 30 минут
            (new DateTimeOffset(2026, 9, 21, 0, 30, 0, TimeSpan.Zero), false, PeakMoment.PeakStart, 30),
            // пн 05:00 UTC — между окнами: до 06:00 один час
            (new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.Zero), false, PeakMoment.PeakStart, 60),
            // пн 12:00 UTC — вне пика, следующее начало во вторник 01:00
            (new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), false, PeakMoment.PeakStart, 13 * 60),
            // сб 02:00 UTC — выходной: пика нет вовсе, следующее начало в пн 01:00 (47 часов)
            (new DateTimeOffset(2026, 9, 26, 2, 0, 0, TimeSpan.Zero), false, PeakMoment.PeakStart, 47 * 60),
            // вс 23:00 UTC — следующий пик в пн 01:00
            (new DateTimeOffset(2026, 9, 27, 23, 0, 0, TimeSpan.Zero), false, PeakMoment.PeakStart, 120),
        };

        Assert.Equal(6, at.Length);
        foreach (var c in at)
        {
            var state = PeakDecisions.State(agent, c.Moment);
            Assert.Equal(c.InPeak, state.InPeak);
            Assert.Equal(c.Kind, state.NextKind);
            Assert.Equal(c.Minutes, state.MinutesUntilNext);
        }
    }

    /// <summary>
    /// В пятницу вечером следующее начало пика — только в понедельник. Проверка не про арифметику,
    /// а про то, что окна ищутся на неделю вперёд: без этого предупреждение «скоро пик» в пятницу
    /// не сработало бы никогда.
    /// </summary>
    [Fact]
    public void В_пятницу_вечером_следующее_начало_пика_в_понедельник()
    {
        var friday = new DateTimeOffset(2026, 9, 25, 23, 0, 0, TimeSpan.Zero);
        var state = PeakDecisions.State(AgentCatalog.DeepSeek, friday);

        Assert.False(state.InPeak);
        Assert.Equal(PeakMoment.PeakStart, state.NextKind);
        Assert.Equal(2 * 24 * 60 + 120, state.MinutesUntilNext);
        Assert.Equal(new DateTime(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc), state.NextSwitchUtc);
    }

    [Fact]
    public void Расписание_переводится_в_местное_время_и_дни_сдвигаются()
    {
        var agent = AgentCatalog.DeepSeek;

        var utc = PeakDecisions.ScheduleText(agent, 0);
        Assert.Contains("UTC+00:00", utc, StringComparison.Ordinal);
        Assert.Contains("01:00–04:00", utc, StringComparison.Ordinal);
        Assert.Contains("06:00–10:00", utc, StringComparison.Ordinal);

        // UTC+03:00: те же окна в 04:00–07:00 и 09:00–13:00, дни не сдвигаются.
        var plus3 = PeakDecisions.ScheduleText(agent, 180);
        Assert.Contains("UTC+03:00", plus3, StringComparison.Ordinal);
        Assert.Contains("04:00–07:00", plus3, StringComparison.Ordinal);
        Assert.Contains("09:00–13:00", plus3, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.PeakDaysWorkWeek, plus3, StringComparison.Ordinal);

        // UTC+12:00: те же окна в 13:00–16:00 и 18:00–22:00, дни те же (сдвига через полночь нет).
        var plus12 = PeakDecisions.ScheduleText(agent, 720);
        Assert.Contains("13:00–16:00", plus12, StringComparison.Ordinal);
        Assert.Contains("18:00–22:00", plus12, StringComparison.Ordinal);

        // UTC−06:00: окно 01:00 UTC приходится на 19:00 ПРЕДЫДУЩЕГО дня — вместе со временем
        // обязаны сдвинуться и дни, иначе «пн 01:00 UTC» показывалось бы как «пн 19:00».
        var minus6 = PeakDecisions.ScheduleText(agent, -360);
        Assert.Contains("19:00–22:00", minus6, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.PeakDaySunday, minus6, StringComparison.Ordinal);

        Assert.Equal("05:00", PeakDecisions.LocalClock(new DateTime(2026, 9, 21, 2, 0, 0, DateTimeKind.Utc), 180));
    }

    /// <summary>
    /// Часовой пояс панель определяет САМА — у системы. Ни в настройках, ни в профиле агента
    /// записанного пояса нет; окна приходят в UTC (так их публикует агент), на экран идут
    /// по местному времени. Проверка сторожит именно это: смещение берётся у системы и на тот
    /// момент, о котором речь (у зон с переводом часов оно по разные стороны границы разное).
    /// </summary>
    [Fact]
    public void Часовой_пояс_берётся_у_системы_и_смещение_спрашивается_на_момент()
    {
        var winter = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var summer = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            (int)TimeZoneInfo.Local.GetUtcOffset(winter).TotalMinutes,
            PeakDecisions.OffsetMinutesAt(winter));
        Assert.Equal(
            (int)TimeZoneInfo.Local.GetUtcOffset(summer).TotalMinutes,
            PeakDecisions.OffsetMinutesAt(summer));

        Assert.NotEqual(string.Empty, PeakDecisions.LocalZoneName());

        // Текущее смещение приходит из местного времени системы, а не из нуля и не из константы.
        Assert.Equal((int)DateTimeOffset.Now.Offset.TotalMinutes, PeakDecisions.OffsetMinutes(DateTimeOffset.Now));

        // И расписание показывается в этой же зоне: подпись UTC совпадает с системным смещением.
        var offset = DateTimeOffset.Now.Offset;
        var expected = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}";
        var text = PeakDecisions.ScheduleText(AgentCatalog.DeepSeek, (int)offset.TotalMinutes);

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Формат_длительности_говорит_словами()
    {
        // Строки длительности — ОБЩИЕ на всю панель (`Span…`), а не «пиковые»: их же читает
        // расписание автокопий. Имя общего ключа и проверяется — иначе двойник заведётся снова.
        Assert.Equal(PanelStrings.SpanLessMinute, PeakDecisions.FormatSpan(0));
        Assert.Contains("30", PeakDecisions.FormatSpan(30), StringComparison.Ordinal);
        Assert.Contains("мин", PeakDecisions.FormatSpan(30), StringComparison.Ordinal);
        Assert.Contains("2", PeakDecisions.FormatSpan(120), StringComparison.Ordinal);
        Assert.Contains("15", PeakDecisions.FormatSpan(135), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- контроллер

    /// <summary>
    /// Прогон без права (проверка без изоляции) не читает файл ключей и не ходит в сеть вовсе:
    /// иначе `--shell-selftest` делал бы запросы с ключом владельца.
    /// </summary>
    [Fact]
    public void Прогон_без_права_не_читает_ключ_и_не_ходит_в_сеть()
    {
        var dir = TempDir();
        try
        {
            // Ключ НА МЕСТЕ и настоящий: иначе проверка была бы слепой — без ключа клиент
            // не вызывается вовсе, и снятый запрет выглядел бы как «всё в порядке».
            // Ровно на этом попалась мутация 24.09.2026.
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(path, "DEEPSEEK_API_KEY: sk-1234567890abcdef\n", new UTF8Encoding(false));

            var client = new FakeClient();
            var controller = Controller(client, new PanelSettings(), path, (_, _, _) => { }, allowed: false);

            controller.Refresh();
            controller.Tick(DateTimeOffset.Now);

            // Ждём завершения, если оно вообще началось: без запрета запрос уходит в фон,
            // и проверка «клиент не вызывался» успела бы пройти до его вызова. Именно на этом
            // попалась мутация 24.09.2026 — проверка была слепой.
            SpinWait.SpinUntil(() => !controller.Busy, 2000);

            Assert.Equal(0, client.Calls);
            Assert.Empty(client.Keys);
            Assert.False(controller.Allowed);
            Assert.Equal(PanelStrings.BalanceLocked, controller.StatusText);
            Assert.Equal(string.Empty, controller.DetailText);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    [Fact]
    public void Баланс_читается_по_ключу_и_показывается()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".credentials.yaml");
            var key = "sk-" + new string('q', 30);
            File.WriteAllText(path, $"refs:\n  DEEPSEEK_API_KEY: {key}\n", new UTF8Encoding(false));

            var client = new FakeClient();
            var controller = Controller(client, new PanelSettings(), path, (_, _, _) => { });

            controller.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "запрос баланса не завершился");

            Assert.Equal(1, client.Calls);
            Assert.Equal(key, client.Keys[0]);
            Assert.Equal("12.34 CNY", controller.StatusText);
            Assert.True(controller.Result.Ok);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Низкий баланс предупреждает ОДИН раз, пока он не поднялся выше порога, — и снова, если
    /// упал опять. Иначе человек получал бы один и тот же шарик каждые пять минут.
    /// </summary>
    [Fact]
    public void Низкий_баланс_предупреждает_один_раз_и_сбрасывается_после_подъёма()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(path, "DEEPSEEK_API_KEY: sk-1234567890abcdef\n", new UTF8Encoding(false));

            var settings = new PanelSettings
            {
                BalanceWarnEnabled = true,
                BalanceWarnThreshold = 20m,
                BalanceAutoRefresh = false,
            };

            var notices = new List<NoticeKind>();
            var client = new FakeClient();
            var controller = Controller(client, settings, path, (kind, _, _) => notices.Add(kind));

            controller.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000));
            Assert.Equal(new[] { NoticeKind.BalanceLow }, notices);

            // Второй запрос с тем же низким балансом — молчим.
            controller.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000));
            Assert.Single(notices);

            // Баланс поднялся выше порога — сброс; затем снова упал — предупреждаем снова.
            client.Next = new BalanceResult(
                true, true, 100m, "CNY", "100.00 CNY", string.Empty, string.Empty, DateTimeOffset.Now, AgentCatalog.DefaultId);
            controller.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000));

            client.Next = new BalanceResult(
                true, true, 5m, "CNY", "5.00 CNY", string.Empty, string.Empty, DateTimeOffset.Now, AgentCatalog.DefaultId);
            controller.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000));

            Assert.Equal(2, notices.Count);
            Assert.All(notices, kind => Assert.Equal(NoticeKind.BalanceLow, kind));
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОРЯДОК: «панель свободна» становится видимым ПОСЛЕДНИМ — после того, как предупреждение
    /// уже отправлено. Проверка держит именно порядок, а не факт отправки.
    ///
    /// Зачем она. В продолжении запроса <c>Busy = false</c> стояло ПЕРВОЙ строкой, а предупреждение
    /// о низком балансе отправлялось последней — то есть наблюдатель, дождавшийся «свободен»
    /// (окно, а следом и проверка), читал состояние, которого ещё не было. Дефект порядка и был
    /// причиной зыбкой проверки: она падала через раз.
    ///
    /// Первая половина ловит дефект НАДЁЖНО, без гонки: в самый момент отправки предупреждения
    /// панель обязана считать себя занятой — иначе «свободен» уже наступил раньше работы.
    /// Вторая половина проверяет то, что видит наблюдатель: дождавшись «свободен», он обязан
    /// видеть отправленное предупреждение.
    /// </summary>
    [Fact]
    public void Свободен_становится_видимым_после_предупреждения()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(path, "DEEPSEEK_API_KEY: sk-1234567890abcdef\n", new UTF8Encoding(false));

            var settings = new PanelSettings
            {
                BalanceWarnEnabled = true,
                BalanceWarnThreshold = 20m,
                BalanceAutoRefresh = false,
            };

            var notices = new List<NoticeKind>();
            var busyWhenNotified = new List<bool>();

            BalanceController? controller = null;

            controller = Controller(new FakeClient(), settings, path, (kind, _, _) =>
            {
                // Панель ещё занята: предупреждение — часть той же работы, а не то, что делается
                // после неё. Сломай порядок — здесь окажется false, и проверка упадёт.
                busyWhenNotified.Add(controller!.Busy);
                notices.Add(kind);
            });

            controller.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "запрос баланса не завершился");

            // Наблюдатель, дождавшийся «свободен», видит предупреждение УЖЕ отправленным.
            Assert.Single(notices);
            Assert.Equal(new[] { true }, busyWhenNotified);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Пик предупреждает за указанное время и ровно один раз на каждое начало. Проверяется
    /// на часах: такт за тактом, как это и происходит в жизни.
    /// </summary>
    [Fact]
    public void Пик_предупреждает_один_раз_за_указанное_время()
    {
        var settings = new PanelSettings
        {
            PeakNotifyMinutes = 30,
            BalanceAutoRefresh = false,
        };

        var notices = new List<(NoticeKind Kind, string Text)>();
        var controller = Controller(
            new FakeClient(), settings, "нет-такого-файла.yaml",
            (kind, _, text) => notices.Add((kind, text)));

        // Такт за 40 минут до начала пика — рано.
        controller.Tick(new DateTimeOffset(2026, 9, 21, 0, 20, 0, TimeSpan.Zero));
        Assert.Empty(notices);

        // Такт за 20 минут — пора.
        controller.Tick(new DateTimeOffset(2026, 9, 21, 0, 40, 0, TimeSpan.Zero));
        Assert.Single(notices);
        Assert.Equal(NoticeKind.PeakApproaching, notices[0].Kind);
        Assert.Contains("20", notices[0].Text, StringComparison.Ordinal);

        // Ещё такт — про это же начало уже сказали.
        controller.Tick(new DateTimeOffset(2026, 9, 21, 0, 50, 0, TimeSpan.Zero));
        Assert.Single(notices);

        // Начался пик — про конец пика не предупреждаем.
        controller.Tick(new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero));
        Assert.Single(notices);
        Assert.True(controller.Peak.InPeak);

        // Вторник: своё начало — своё предупреждение.
        controller.Tick(new DateTimeOffset(2026, 9, 22, 0, 40, 0, TimeSpan.Zero));
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public void Автообновление_идёт_по_периоду_а_пик_считается_каждый_такт()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(path, "DEEPSEEK_API_KEY: sk-1234567890abcdef\n", new UTF8Encoding(false));

            var settings = new PanelSettings { BalanceAutoRefresh = true, BalanceRefreshMinutes = 5 };
            var client = new FakeClient();
            var controller = Controller(client, settings, path, (_, _, _) => { });

            var start = new DateTimeOffset(2026, 9, 21, 0, 20, 0, TimeSpan.Zero);

            controller.Tick(start);
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "первый запрос не завершился");
            Assert.Equal(1, client.Calls);

            // Через минуту — рано для баланса, но пик уже пересчитан.
            controller.Tick(start.AddMinutes(1));
            Assert.Equal(1, client.Calls);
            Assert.False(controller.Peak.InPeak);

            controller.Tick(start.AddMinutes(6));
            Assert.True(SpinWait.SpinUntil(() => !controller.Busy, 5000), "второй запрос не завершился");
            Assert.Equal(2, client.Calls);

            // А пик при этом виден: 00:40 UTC — до начала окна 01:00 двадцать минут.
            controller.Tick(new DateTimeOffset(2026, 9, 21, 0, 40, 0, TimeSpan.Zero));
            Assert.Equal(PeakMoment.PeakStart, controller.Peak.NextKind);
            Assert.Equal(20, controller.Peak.MinutesUntilNext);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    [Fact]
    public void Подсказка_значка_показывает_агента_баланс_и_тариф()
    {
        var tooltip = TrayTooltip.Build("DeepSeek", "12.34 CNY", PanelStrings.PeakOffPeak);

        Assert.Contains("DeepSeek", tooltip, StringComparison.Ordinal);
        Assert.Contains("12.34 CNY", tooltip, StringComparison.Ordinal);
        Assert.Contains(PanelStrings.PeakOffPeak, tooltip, StringComparison.Ordinal);

        Assert.Contains(
            PanelStrings.TrayToolTipNoBalance,
            TrayTooltip.Build("DeepSeek", "", PanelStrings.PeakInPeak),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- что видит человек

    /// <summary>Окно панели показывает то, что дал контроллер: строки, а не свои догадки.</summary>
    [AvaloniaFact]
    public void Окно_панели_показывает_баланс_и_пик()
    {
        var window = new MainWindow();
        var balance = new FakeBalance();
        window.AttachBalance(balance);

        Assert.Equal("12.34 CNY", window.FindControl<TextBlock>("BalanceStatusText")!.Text);
        Assert.Equal(PanelStrings.PeakOffPeak, window.FindControl<TextBlock>("PeakText")!.Text);
        Assert.Contains("13:00", window.FindControl<TextBlock>("PeakNextText")!.Text!, StringComparison.Ordinal);
        // ⚠️ В главном окне — КОРОТКАЯ строка, и её форма изменилась 27.09.2026 по словам владельца
        // («проверено и дата с временем нужно… остальная информация не нужна в принципе»):
        // прежде здесь стояло полное расписание с источником. Теперь это ровно то, что дал
        // контроллер, — дата проверки и ничего больше.
        Assert.Equal(balance.PeakCheckedText, window.FindControl<TextBlock>("PeakScheduleText")!.Text);

        window.FindControl<Button>("RefreshBalanceButton")!
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, balance.Refreshes);
    }

    /// <summary>
    /// Настройки баланса сохраняются и ограничиваются: файл правят руками, а период в 0 минут
    /// означал бы запрос каждую секунду.
    /// </summary>
    [Fact]
    public void Настройки_баланса_сохраняются_и_ограничиваются()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));

            Assert.True(store.Save(new PanelSettings
            {
                ActiveAgent = "нет-такого-агента",
                BalanceAutoRefresh = false,
                BalanceRefreshMinutes = 0,
                BalanceWarnEnabled = true,
                BalanceWarnThreshold = -5m,
                PeakNotifyMinutes = 99999,
            }));

            var back = store.Load().Settings;

            Assert.Equal(AgentCatalog.DefaultId, back.ActiveAgent);
            Assert.False(back.BalanceAutoRefresh);
            Assert.Equal(PanelSettings.RefreshMinutesMin, back.BalanceRefreshMinutes);
            Assert.True(back.BalanceWarnEnabled);
            Assert.Equal(0m, back.BalanceWarnThreshold);
            Assert.Equal(PanelSettings.PeakNotifyMinutesMax, back.PeakNotifyMinutes);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Окно настроек ставит НАСТРОЙКИ раздела «Баланс и тариф» из настроек и отдаёт их обратно
    /// при сохранении.
    ///
    /// ⚠️ Расписания пиков и таблицы цен здесь больше НЕТ — их убрали решением владельца 28.09.2026
    /// (для них есть окно «Пики и тарифы»). Проверка переписана: она сторожит и то, что настройки
    /// работают, и то, что показ тарифа в этот раздел не вернулся.
    /// </summary>
    [AvaloniaFact]
    public void Окно_настроек_работает_с_полями_баланса_и_тарифа()
    {
        var settings = new FakeSettings(new PanelSettings
        {
            ActiveAgent = AgentCatalog.DefaultId,
            BalanceAutoRefresh = true,
            BalanceRefreshMinutes = 7,
            BalanceWarnEnabled = true,
            BalanceWarnThreshold = 15m,
            PeakNotifyMinutes = 15,
        });

        var window = new SettingsWindow();
        window.Attach(settings, autostart: null, balance: new FakeBalance());

        var agent = window.FindControl<ComboBox>("AgentBox")!;
        Assert.Equal(AgentCatalog.IndexOf(AgentCatalog.DefaultId), agent.SelectedIndex);
        Assert.Equal(AgentCatalog.All.Count, agent.ItemCount);

        Assert.True(window.FindControl<CheckBox>("BalanceAutoCheck")!.IsChecked);
        Assert.Equal(7m, window.FindControl<NumericUpDown>("RefreshMinutesBox")!.Value);
        Assert.True(window.FindControl<CheckBox>("BalanceWarnCheck")!.IsChecked);
        Assert.Equal(15m, window.FindControl<NumericUpDown>("ThresholdBox")!.Value);
        Assert.Equal(15m, window.FindControl<NumericUpDown>("PeakNotifyBox")!.Value);

        // Показа тарифа в разделе нет: расписание и цены живут в своём окне.
        Assert.Null(window.FindControl<TextBlock>("PeakScheduleText"));
        Assert.Null(window.FindControl<Grid>("PeakTablePanel"));
        Assert.Null(window.FindControl<Grid>("PriceTablePanel"));

        // Человек поменял значения — они уходят в сохранение.
        window.FindControl<NumericUpDown>("PeakNotifyBox")!.Value = 30;
        window.FindControl<CheckBox>("BalanceAutoCheck")!.IsChecked = false;
        window.FindControl<Button>("SaveButton")!
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(settings.SavedSettings);
        Assert.Equal(30, settings.SavedSettings!.PeakNotifyMinutes);
        Assert.False(settings.SavedSettings.BalanceAutoRefresh);
        Assert.Equal(15m, settings.SavedSettings.BalanceWarnThreshold);
    }

    // ---------------------------------------------------------------- смена агента в главном окне

    /// <summary>
    /// АКТИВНЫЙ АГЕНТ ВИДЕН В ГЛАВНОМ ОКНЕ — там же, где его баланс, и список честен насчёт
    /// выбора: пока агент ОДИН, выпадающий список недоступен и говорит об этом словами.
    ///
    /// Слова владельца 27.09.2026: *«активный агент так же должен быстро меняться в главном окне,
    /// где указан баланс, наверное там должен быть выпадающий список»*. Обратная сторона того же
    /// решения: живой список из одного пункта обещал бы выбор, которого нет.
    /// </summary>
    [AvaloniaFact]
    public void В_главном_окне_виден_активный_агент_и_список_не_врёт_о_выборе()
    {
        var settings = new FakeSettings(new PanelSettings { ActiveAgent = AgentCatalog.DefaultId });
        var window = new MainWindow();

        window.AttachAgentChoice(settings);
        window.AttachBalance(new FakeBalance());

        var box = window.FindControl<ComboBox>("AgentBox");
        var hint = window.FindControl<TextBlock>("AgentHintText");
        var label = window.FindControl<TextBlock>("ActiveAgentLabel");

        Assert.NotNull(box);
        Assert.NotNull(hint);
        Assert.NotNull(label);

        Assert.Equal(AgentCatalog.All.Count, box!.ItemCount);
        Assert.Equal(AgentCatalog.IndexOf(AgentCatalog.DefaultId), box.SelectedIndex);

        // Подпись — та же строка, что у того же решения в настройках: одно решение — одно имя.
        Assert.Equal(PanelStrings.SettingsActiveAgentLabel, label!.Text);

        // И ЧЕСТНО: выбор один — список недоступен, а под ним сказано, почему.
        Assert.False(AgentCatalog.HasChoice, "в каталоге появился второй агент — проверка устарела");
        Assert.False(box.IsEnabled, "список из одного агента выглядит как выбор, которого нет");
        Assert.Equal(PanelStrings.SettingsActiveAgentHint, hint!.Text);
    }

    /// <summary>
    /// СМЕНА АГЕНТА ИДЁТ ЧЕРЕЗ ВЛАДЕЛЬЦА НАСТРОЕК — не вторым способом записи. И после неё
    /// контроллеру сказано перечитать баланс и окна пика: иначе человек полминуты (до такта часов)
    /// видел бы баланс ПРЕЖНЕГО агента под именем нового.
    ///
    /// ⚠️ Смена идёт номером в списке, а не щелчком: пока агент в каталоге один, щёлкнуть не по
    /// чему. Имя в настройках при этом ЧУЖОЕ (файл правят руками) — ровно тот случай, в котором
    /// выбор в списке и настройки расходятся, и панель обязана записать то, что человек видит.
    /// </summary>
    [AvaloniaFact]
    public void Смена_агента_записывается_владельцем_настроек_и_перечитывает_баланс()
    {
        var settings = new FakeSettings(new PanelSettings { ActiveAgent = "мусор-из-файла" });
        var balance = new FakeBalance();

        var window = new MainWindow();
        window.AttachAgentChoice(settings);
        window.AttachBalance(balance);

        Assert.True(window.ChooseAgent(AgentCatalog.IndexOf(AgentCatalog.DefaultId)), "смена не записалась");

        // Запись прошла ТЕМ ЖЕ путём, что кнопка «Сохранить»: копия настроек, правка, сохранение.
        Assert.NotNull(settings.SavedSettings);
        Assert.Equal(AgentCatalog.DefaultId, settings.SavedSettings!.ActiveAgent);
        Assert.Equal(AgentCatalog.DefaultId, settings.Settings.ActiveAgent);

        // И контроллеру сказано перечитать: баланс нового агента, а не прежнего. То, что за этим
        // следует настоящий запрос, проверяется на самом контроллере
        // («Смена_агента_сбрасывает_прежний_ответ_и_спрашивает_заново»): оконная заглушка
        // о запросе ничего не знает и врала бы, если бы её об этом спросили.
        Assert.Equal(1, balance.AgentChanges);

        // Повторный выбор того же агента НИЧЕГО не записывает: список не «долбит» настройки.
        Assert.False(window.ChooseAgent(AgentCatalog.IndexOf(AgentCatalog.DefaultId)));
        Assert.Equal(1, balance.AgentChanges);
    }

    /// <summary>
    /// ЗАПИСАТЬ НЕ УДАЛОСЬ — ОКНО ГОВОРИТ ЭТО ПРЯМО и возвращает список на то, что стоит
    /// в настройках: несохранённое значение в списке показывало бы выбор, которого панель
    /// не приняла (прогон проверки настроек не меняет вовсе).
    /// </summary>
    [AvaloniaFact]
    public void Если_записать_агента_не_удалось_список_возвращается_и_окно_говорит()
    {
        var settings = new FakeSettings(new PanelSettings { ActiveAgent = "мусор-из-файла" }) { Writable = false };
        var balance = new FakeBalance();

        var window = new MainWindow();
        window.AttachAgentChoice(settings);
        window.AttachBalance(balance);

        Assert.False(window.ChooseAgent(AgentCatalog.IndexOf(AgentCatalog.DefaultId)));

        Assert.Null(settings.SavedSettings);
        Assert.Equal("мусор-из-файла", settings.Settings.ActiveAgent);
        Assert.Equal(0, balance.AgentChanges);

        var hint = window.FindControl<TextBlock>("AgentHintText");
        Assert.NotNull(hint);
        Assert.Equal(PanelStrings.SettingsSaveFailed, hint!.Text);

        var box = window.FindControl<ComboBox>("AgentBox");
        Assert.NotNull(box);
        Assert.Equal(AgentCatalog.IndexOf("мусор-из-файла"), box!.SelectedIndex);
    }

    /// <summary>
    /// БЕЗ ВЛАДЕЛЬЦА НАСТРОЕК СПИСОК НЕДОСТУПЕН И ГОВОРИТ ПОЧЕМУ: так выглядит главное окно
    /// в прогоне проверки и на съёмке кадра — там настройки не читаются и не меняются.
    /// </summary>
    [AvaloniaFact]
    public void Без_владельца_настроек_смена_агента_недоступна_и_объяснена()
    {
        var window = new MainWindow();

        var box = window.FindControl<ComboBox>("AgentBox");
        var hint = window.FindControl<TextBlock>("AgentHintText");

        Assert.NotNull(box);
        Assert.NotNull(hint);

        Assert.False(box!.IsEnabled);
        Assert.Equal(PanelStrings.SettingsReadOnlyNote, hint!.Text);
        Assert.False(window.ChooseAgent(0));
    }

    /// <summary>
    /// СМЕНА АГЕНТА ПЕРЕЧИТЫВАЕТ БАЛАНС И ОКНА ПИКА — на самом контроллере, а не на заглушке.
    ///
    /// Три обещания и одна беда у каждого:
    ///
    /// * **прежний ответ сбрасывается**: он про ДРУГОГО агента, и «12.34 CNY» под новым именем
    ///   читалось бы как его баланс;
    /// * **запрос идёт заново** — иначе человек до такта часов (полминуты) видел бы «не спрашивали»;
    /// * **окна пика пересчитаны по новому профилю** и подсказка значка разбужена (<c>Changed</c>):
    ///   подсказка живёт на этом событии, и без него на значке остался бы прежний тариф.
    ///
    /// ⚠️ **Чем эта проверка была слепа** (падение на раннере 36638902335, разобрано 29.09.2026).
    /// Она читала <c>Result</c> как обычное поле, а заполняет его ФОНОВОЕ продолжение запроса.
    /// Отсюда три разных падения одной и той же проверки, все — потерянная гонка:
    ///
    /// * «Assert.True() Failure» без строки (первое утверждение): ответ ещё не дошёл, а проверка
    ///   ждала только, что запрос УШЁЛ (<c>client.Calls == 1</c>) — это разные события;
    /// * «прежний ответ остался — он про другого агента»: на панели в этот миг был чужой ответ,
    ///   но строка отказа не различала ДВУХ разных вещей — «ответ нового агента уже дошёл»
    ///   и «ответ ПРЕЖНЕГО агента, ушедший до смены, дописался после сброса». Второе — настоящий
    ///   дефект продукта (тогда продолжение запроса применяло ответ, не сверяя, тот ли это агент),
    ///   и теперь его стережёт нижняя проверка: <c>Смена_агента_во_время_запроса…</c>. Различить
    ///   эти две вещи прежней проверкой было нельзя ещё и потому, что подставной клиент отвечал
    ///   ОДНОЙ И ТОЙ ЖЕ суммой на любой запрос, а «другой-агент» в каталоге из одного агента —
    ///   тот же профиль: у ответов не было ни одного признака «чей он»;
    /// * «баланс нового агента не запрошен» (ждали 5 с): смена случилась, пока запрос был в пути,
    ///   и уходила в молчаливый пропуск «панель занята».
    ///
    /// Поэтому теперь ответ нового агента ДЕРЖИТ клиент, пока проверка смотрит на панель, ответы
    /// прежнего и нового агента РАЗНЫЕ, а ждут не «запрос начался», а «ответ дошёл и панель
    /// свободна». Гонки в проверке не остаётся — остаётся ровно то состояние, о котором она.
    /// </summary>
    [Fact]
    public void Смена_агента_сбрасывает_прежний_ответ_и_спрашивает_заново()
    {
        var dir = TempDir();
        var client = new FakeClient();

        try
        {
            // Ключ на месте: без файла ключа запрос до клиента не доходит вовсе, и «баланс
            // перечитан» проверялось бы на пустом месте.
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, ".credentials.yaml"),
                "DEEPSEEK_API_KEY: sk-1234567890abcdef\n",
                new UTF8Encoding(false));

            var at = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

            // У прежнего агента и у нового — РАЗНЫЕ суммы: по одной строке видно, чей ответ показан.
            client.Answer = call => new BalanceResult(
                true,
                true,
                call == 1 ? 12.34m : 99.99m,
                "CNY",
                call == 1 ? "12.34 CNY" : "99.99 CNY",
                string.Empty,
                string.Empty,
                at,
                AgentCatalog.DefaultId);

            // Ответ НОВОГО агента держим, пока не посмотрим на панель.
            client.HoldCalls.Add(2);

            // Автообновление баланса выключено — спрашиваем сами, одним запросом: так у первого
            // запроса нет двойника (прежде такт часов и явная просьба могли дать ДВА запроса,
            // и «второй» в проверке оказывался не тем, о котором она думала).
            var settings = new PanelSettings
            {
                ActiveAgent = AgentCatalog.DefaultId,
                BalanceAutoRefresh = false,
            };

            var controller = Controller(
                client,
                settings,
                Path.Combine(dir, ".credentials.yaml"),
                (_, _, _) => { },
                now: at);

            var changed = 0;
            controller.Changed += () => changed++;

            // Часы: окна пика посчитаны; баланс спрашиваем сами.
            controller.Tick(at);
            controller.Refresh();

            // Ждём ПРИХОДА ответа, а не начала запроса.
            Assert.True(
                SpinWait.SpinUntil(() => !controller.Busy && controller.Result.Ok, 5000),
                "первый запрос баланса не прошёл");
            Assert.Equal(1, client.Calls);
            Assert.Equal("12.34 CNY", controller.StatusText);
            Assert.False(controller.Peak.InPeak);

            var before = changed;

            // Владелец настроек записал ДРУГОГО агента (сюда его пишет главное окно).
            settings.ActiveAgent = "другой-агент";
            controller.AgentChanged();

            // Прежний ответ сброшен СРАЗУ, до ответа сети: показывать чужой баланс нельзя. Ответ
            // нового агента в этот миг ещё держит клиент, поэтому видеть здесь можно ровно одно —
            // «не спрашивали»: ни суммы прежнего агента, ни суммы нового.
            Assert.False(controller.Result.Ok, "прежний ответ остался — он про другого агента");
            Assert.Equal(PanelStrings.BalanceNotRequested, controller.StatusText);

            Assert.True(changed > before, "подсказка значка не разбужена: событие «состояние изменилось» не пришло");

            // И запрос ушёл заново — баланс НОВОГО агента. Приход запроса в клиент ждём явно,
            // с отказом по времени: «сколько успеется» здесь и было вторым лицом той же гонки.
            Assert.True(SpinWait.SpinUntil(() => client.Calls == 2, 5000), "баланс нового агента не запрошен");

            // Отпускаем ответ нового агента: на панели обязан оказаться ЕГО баланс.
            client.ReleaseAnswer();

            Assert.True(
                SpinWait.SpinUntil(() => !controller.Busy && controller.Result.Ok, 5000),
                "ответ нового агента не дошёл");
            Assert.Equal("99.99 CNY", controller.StatusText);
            Assert.Equal(AgentCatalog.DefaultId, controller.Agent.Id);
        }
        finally
        {
            // Удержанные ответы отпускаем и при падении: иначе фоновые нитки ждали бы их впустую.
            client.ReleaseAnswer();
            client.ReleaseAnswer();
            RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СМЕНА АГЕНТА ВО ВРЕМЯ ЗАПРОСА: ответ прежнего агента не встаёт под именем нового.
    ///
    /// Это тот самый случай, ради которого дверь смены агента и сделана, и он живой: запрос баланса
    /// идёт по сети сотни миллисекунд, и человек успевает переключить агента, пока ответ летит.
    /// Сброса ответа здесь мало — продолжение запроса дописывает <c>Result</c> ПОСЛЕ сброса.
    /// До правки 29.09.2026 это выглядело так: человек переключил агента, увидел «не спрашивали»,
    /// а через мгновение — баланс ПРЕЖНЕГО агента под именем нового; и так до следующего такта
    /// часов (до пяти минут), потому что запрос за нового агента в это время МОЛЧА пропускался
    /// («панель занята» — <c>BalanceController.Refresh</c>).
    ///
    /// ⚠️ Проверка смотрит на панель в ТОТ САМЫЙ миг: ответ прежнего агента уже отпущен, а ответ
    /// нового ещё стоит за дверью клиента. Смотреть на итог (после обоих ответов) бесполезно —
    /// так дефект ПРОХОДИТ незамеченным: ответ нового агента затирает ответ прежнего, и на панели
    /// оказывается верная сумма. Именно на этом первая редакция этой проверки и выжила мутацию.
    /// </summary>
    [Fact]
    public void Смена_агента_во_время_запроса_не_показывает_ответ_прежнего()
    {
        var dir = TempDir();
        var client = new FakeClient();

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, ".credentials.yaml"),
                "DEEPSEEK_API_KEY: sk-1234567890abcdef\n",
                new UTF8Encoding(false));

            var at = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

            client.Answer = call => new BalanceResult(
                true,
                true,
                call == 1 ? 12.34m : 99.99m,
                "CNY",
                call == 1 ? "12.34 CNY" : "99.99 CNY",
                string.Empty,
                string.Empty,
                at,
                AgentCatalog.DefaultId);

            // Держим ОБА ответа: прежнего агента и нового. Отпускаем их по одному — иначе не увидеть
            // состояние «ответ прежнего уже пришёл, ответ нового ещё нет».
            client.HoldCalls.Add(1);
            client.HoldCalls.Add(2);

            var settings = new PanelSettings
            {
                ActiveAgent = AgentCatalog.DefaultId,
                BalanceAutoRefresh = false,
            };

            var controller = Controller(
                client,
                settings,
                Path.Combine(dir, ".credentials.yaml"),
                (_, _, _) => { },
                now: at);

            controller.Refresh();

            Assert.True(SpinWait.SpinUntil(() => client.Calls == 1, 5000), "запрос баланса не ушёл");
            Assert.True(controller.Busy, "панель считает себя свободной, пока ответ не пришёл");

            // Человек переключил агента, пока ответ летел.
            settings.ActiveAgent = "другой-агент";
            controller.AgentChanged();

            Assert.False(controller.Result.Ok, "прежний ответ остался — он про другого агента");

            // Отпускаем ответ ПРЕЖНЕГО агента. Ответ нового агента при этом ещё стоит за дверью —
            // значит следующий взгляд на панель видит ровно то, что оставил прежний агент.
            client.ReleaseAnswer();

            Assert.True(SpinWait.SpinUntil(() => client.Calls == 2, 5000), "запрос баланса нового агента не ушёл");
            Assert.False(controller.Result.Ok, "ответ прежнего агента встал под именем нового");
            Assert.Equal(PanelStrings.BalanceNotRequested, controller.StatusText);

            // Теперь отпускаем ответ нового агента: на панели обязан оказаться ЕГО баланс.
            client.ReleaseAnswer();

            Assert.True(
                SpinWait.SpinUntil(() => !controller.Busy && controller.Result.Ok, 5000),
                "баланс нового агента не дошёл");
            Assert.Equal("99.99 CNY", controller.StatusText);
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            client.ReleaseAnswer();
            client.ReleaseAnswer();
            RemoveTemp(dir);
        }
    }
}
