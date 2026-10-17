using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЗАМКИ ДВИЖКА: решение о состоянии замка, уборка МЁРТВЫХ и неприкосновенность ЖИВЫХ.
///
/// **Откуда взялось.** Разведка этапа B (10.10.2026) замерила механизм на самом движке и опытом
/// с убитым держателем: замок — файл-сосед `&lt;файл&gt;.lock` с номером процесса внутри; держатель
/// снимает его сам, но убитый (`taskkill`) оставляет навсегда, и следующий писатель получает отказ
/// по времени. Сам движок свой замок не убирает никогда — он говорит прямо, что это дело человека,
/// «убедившись, что владельца нет». Панель ведёт себя как тот самый осторожный человек: убирает
/// ТОЛЬКО мёртвый замок.
///
/// ⚠️ Проверки идут по обеим сторонам, и это не формальность: проверка «мёртвый убран» без парной
/// «живой цел» доказывала бы лишь то, что панель чистит ВСЁ ПОДРЯД, — то есть ровно тот дефект,
/// ради которого правило и заведено.
///
/// Дом движка — временный каталог, настоящий `~/.dsh` не читается и не пишется. Живость процессов
/// в проверках конструирования ПОДСТАВНАЯ (иначе проверка зависела бы от того, какие номера
/// выдала система); настоящая проверка живости испытана отдельно — на себе и на завершённом
/// процессе.
/// </summary>
public class EngineLocksTests
{
    /// <summary>Дом движка для проверок: свой временный каталог, убирается за собой.</summary>
    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static void Plant(string home, string relative, string? text)
    {
        var lockPath = EngineLocks.LockPath(home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        if (text is not null) File.WriteAllText(lockPath, text);
    }

    private static Func<int, ProcessFacts.Liveness> Liveness(int livePid) =>
        pid => pid == livePid ? ProcessFacts.Liveness.Alive : ProcessFacts.Liveness.Dead;

    // ------------------------------------------------------------------ решение о состоянии

    /// <summary>
    /// ЧИСТОЕ РЕШЕНИЕ по содержимому файла: и все пять состояний, и — главное — разница между
    /// «замка нет» и «в файле мусор». Путать их нельзя: первое — молчание, второе — слова человеку.
    /// </summary>
    [Theory]
    [InlineData(null, false, EngineLockState.Missing)]      // файла нет
    [InlineData("", true, EngineLockState.Unreadable)]      // файл есть, номера в нём нет
    [InlineData("   ", true, EngineLockState.Unreadable)]   // пробелы — тоже не номер
    [InlineData("не число", true, EngineLockState.Unreadable)]
    [InlineData("0", true, EngineLockState.Unreadable)]     // нулевого процесса не бывает
    [InlineData("-5", true, EngineLockState.Unreadable)]
    [InlineData("4242", true, EngineLockState.Stale)]       // владелец мёртв
    [InlineData("5150", true, EngineLockState.Live)]        // владелец жив
    [InlineData(" 4242\n", true, EngineLockState.Stale)]    // как пишет движок: номер и перевод строки
    public void Состояние_замка_по_содержимому_файла(string? text, bool existed, EngineLockState expected)
    {
        var state = EngineLocks.Decide(text, existed, Liveness(livePid: 5150));

        Assert.Equal(expected, state);
    }

    /// <summary>
    /// «СПРОСИТЬ СИСТЕМУ НЕ УДАЛОСЬ» — отдельное состояние, и оно НЕ значит «мёртв»: снести замок
    /// живого, но защищённого процесса значило бы испортить чужую работу. Здесь третья сторона
    /// проверки: не только «убран» и «цел», но и «сомневаюсь — не трогаю».
    /// </summary>
    [Fact]
    public void Неизвестная_живость_владельца_значит_не_трогать()
    {
        var state = EngineLocks.Decide("4242", existed: true, _ => ProcessFacts.Liveness.Unknown);

        Assert.Equal(EngineLockState.Unverifiable, state);
    }

    /// <summary>
    /// ОСМОТР ДОМА ДВИЖКА: мёртвый замок виден с путём и номером, живой — тоже, а пустого места
    /// в списке нет (замка нет — не о чем говорить). Заодно видно, что осмотр смотрит ИМЕННО те
    /// файлы, которые движок запирает по замеру, — включая КАТАЛОГ `profiles/node_modules`.
    /// </summary>
    [Fact]
    public void Осмотр_дома_находит_мёртвый_и_живой_замки_и_молчит_о_пустом()
    {
        var home = NewHome();

        try
        {
            Plant(home, "settings.yaml", "4242");
            Plant(home, Path.Combine("profiles", "node_modules"), "5150\n");

            var found = EngineLocks.Inspect(home, Liveness(livePid: 5150));

            Assert.Equal(2, found.Count);

            var stale = Assert.Single(found, item => item.State == EngineLockState.Stale);
            Assert.Equal(4242, stale.Pid);
            Assert.EndsWith("settings.yaml.lock", stale.Path, StringComparison.Ordinal);

            var live = Assert.Single(found, item => item.State == EngineLockState.Live);
            Assert.Equal(5150, live.Pid);
            Assert.EndsWith("node_modules.lock", live.Path, StringComparison.Ordinal);

            // Ключей и .credentials в списке нет — их замков в доме не лежит.
            Assert.DoesNotContain(found, item => item.Path.Contains(".credentials", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// НАСТОЯЩАЯ ПРОВЕРКА ЖИВОСТИ (без подстановок): себя видит живым, номер 0 — мёртвым,
    /// а завершённый процесс — мёртвым. Без этой проверки подстановки выше доказывали бы только
    /// то, что панель умеет читать то, что ей подсунули.
    /// </summary>
    [Fact]
    public void Настоящая_проверка_живости_различает_живого_и_мёртвого()
    {
        Assert.Equal(ProcessFacts.Liveness.Alive, ProcessFacts.LivenessOf(Environment.ProcessId));
        Assert.Equal(ProcessFacts.Liveness.Dead, ProcessFacts.LivenessOf(0));
        Assert.Equal(ProcessFacts.Liveness.Dead, ProcessFacts.LivenessOf(-1));

        using var finished = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        finished.WaitForExit();
        Assert.Equal(ProcessFacts.Liveness.Dead, ProcessFacts.LivenessOf(finished.Id));
    }

    // ------------------------------------------------------------------ уборка при подъёме сервера

    /// <summary>
    /// Поддельный движок, который живёт, пока его не погасят: тот же приём, что в
    /// <c>OwnServerTests</c> (там он разобран подробно) — <c>cmd.exe</c>, занятый пингом петли.
    /// </summary>
    private static DshEngine LiveEngine() => new(
        Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        $"/c {Path.Combine(Environment.SystemDirectory, "ping.exe")} -n 30 127.0.0.1 >nul & rem");

    /// <summary>
    /// Подставная разведка: на машине проверок РАБОТАЕТ настоящий сервер владельца на 3080, и с
    /// настоящей таблицей портов панель честно отказалась бы поднимать свой сервер («замок от
    /// второго движка»). Здесь разведка отвечает «нашла пусто» — проверке нужен не поиск, а подъём.
    /// Живость процессов при этом ПОДСТАВНАЯ, как и во всех проверках этого класса.
    /// </summary>
    private static ServerProbe EmptyProbe(int livePid) => ServerProbe.System() with
    {
        ListenerPid = _ => 0,
        Listeners = () => Array.Empty<PortTable.Listener>(),
        CommandLine = _ => string.Empty,
        ProcessName = _ => string.Empty,
        StartTicks = _ => 0,
        AnswersFingerprint = _ => false,
        Liveness = Liveness(livePid),
    };

    private static ServerController Controller(string home, List<string> log, bool mayClear, int livePid) =>
        new(
            home,
            () => home,
            log.Add,
            mayOccupyOwnerPort: false,
            locateEngine: LiveEngine,
            requestedPort: () => FreePort.Find(),
            probe: EmptyProbe(livePid),
            stopWait: TimeSpan.Zero,
            scanInterval: TimeSpan.Zero,
            mayClearEngineLocks: mayClear);

    /// <summary>
    /// ОБЕ СТОРОНЫ РАЗОМ, на настоящем подъёме сервера: мёртвый замок УБРАН и назван строкой
    /// с путём и номером, живой замок ЦЕЛ и тоже назван.
    ///
    /// ⚠️ Именно вместе, а не двумя проверками: так видно, что дело не в «панель чистит всё»
    /// и не в «панель не чистит ничего».
    /// </summary>
    [Fact]
    public void При_подъёме_сервера_мёртвый_замок_убирается_а_живой_остаётся()
    {
        var home = NewHome();
        var log = new List<string>();

        try
        {
            Plant(home, "settings.yaml", "4242");
            Plant(home, ".credentials.yaml", "5150");

            var deadPath = EngineLocks.LockPath(home, "settings.yaml");
            var livePath = EngineLocks.LockPath(home, ".credentials.yaml");

            using var controller = Controller(home, log, mayClear: true, livePid: 5150);

            controller.Start(TimeSpan.FromSeconds(1));

            // Мёртвый — убран, и о нём сказано словами: путь и номер мёртвого процесса.
            Assert.False(File.Exists(deadPath));

            var removed = Assert.Single(log, line => line.Contains("проверенный набор версий сервера DSH снят", StringComparison.Ordinal));
            Assert.Contains(deadPath, removed, StringComparison.Ordinal);
            Assert.Contains("4242", removed, StringComparison.Ordinal);

            // Живой — ЦЕЛ, содержимое то же, и о нём тоже сказано.
            Assert.True(File.Exists(livePath));
            Assert.Equal("5150", File.ReadAllText(livePath).Trim());

            var kept = Assert.Single(log, line => line.Contains("проверенный набор версий сервера DSH не тронут", StringComparison.Ordinal));
            Assert.Contains(livePath, kept, StringComparison.Ordinal);
            Assert.Contains("5150", kept, StringComparison.Ordinal);

            // И на каждый замок — РОВНО одна строка, а не по строке на такт.
            Assert.Single(log, line => line.Contains(deadPath, StringComparison.Ordinal));
            Assert.Single(log, line => line.Contains(livePath, StringComparison.Ordinal));

            controller.Stop(confirmed: true);
        }
        finally
        {
            TryRemove(home);
        }
    }

    /// <summary>
    /// БЕЗ ПРАВА — НЕ ТРОГАЕТСЯ НИЧЕГО, и об этом сказано: молчание выглядело бы как «замков
    /// не было». Право — то же, что у файлов состояния панели (<c>RunRights.LocalData</c>).
    /// </summary>
    [Fact]
    public void Без_права_замки_не_трогаются_и_об_этом_сказано()
    {
        var home = NewHome();
        var log = new List<string>();

        try
        {
            Plant(home, "settings.yaml", "4242");

            var deadPath = EngineLocks.LockPath(home, "settings.yaml");

            using var controller = Controller(home, log, mayClear: false, livePid: 5150);

            controller.Start(TimeSpan.FromSeconds(1));

            Assert.True(File.Exists(deadPath));
            Assert.Equal("4242", File.ReadAllText(deadPath).Trim());

            Assert.Contains(log, line => line.Contains("права на данные панели", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains(deadPath, StringComparison.Ordinal));

            controller.Stop(confirmed: true);
        }
        finally
        {
            TryRemove(home);
        }
    }

    /// <summary>Убрать временный дом: движок мог не успеть отпустить файлы — это не провал проверки.</summary>
    private static void TryRemove(string home)
    {
        try
        {
            Directory.Delete(home, recursive: true);
        }
        catch
        {
            // Занято — уберёт система; проверка не о уборке мусора.
        }
    }
}
