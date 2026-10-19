using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЖУРНАЛ ЗАВЕРШЕНИЯ ДВИЖКА: «УПАЛ» И «ПОГАСИЛИ» РАЗЛИЧАЮТСЯ ПОСЛЕ ФАКТА (t15, 18.10.2026).
///
/// **От какого случая.** 18.10.2026 в 15:40:19 умер сервер DSH на 3080, и разобрать инцидент
/// было нечем: панель запоминала вывод движка, но на завершении не печатала ни кода выхода,
/// ни хвоста, ни того, ГАСИЛА она процесс или он ушёл сам. Три разных события выглядели одинаково.
///
/// **На чём проверяется.** На ПОДСТАВНОМ процессе (<c>cmd /c … exit 7</c>) и на подставных
/// фактах — живой сервер здесь не нужен и не запускается: у панели нет способа подсунуть свою
/// командную строку живому движку, а гасить настоящий сервер проверка не имеет права.
///
/// **Мутации, которые обязаны упасть:** «код выхода не печатается» (ZH1) · «хвост не маскируется»
/// (ZH2) · «всегда пишется «остановлен панелью»» (ZH3) · «хвост не ограничен сорока строками» (ZH4) ·
/// «контроллер не пишет запись в свой журнал» (ZH5).
/// </summary>
public sealed class EngineExitJournalTests
{
    /// <summary>Момент инцидента — литералом: время в записи обязано быть МОМЕНТОМ, а не «сейчас».</summary>
    private static readonly DateTimeOffset At =
        new(2026, 10, 18, 15, 40, 19, TimeSpan.FromHours(3));

    /// <summary>Код выхода с «редкими» цифрами: такие не встретятся ни в PID, ни в дате.</summary>
    private const int Code = 4711;

    [Fact]
    public void Журнал_называет_код_выхода_и_чем_завершён_процесс()
    {
        var (exitCode, raw) = RunStub("echo gotovo & echo beda 1>&2 & exit " + Code);

        // Подставной процесс действительно завершился этим кодом и действительно печатал:
        // без этих двух утверждений проверка ниже мерила бы пустоту.
        Assert.Equal(Code, exitCode);
        Assert.False(raw.Count == 0, "подставной процесс ничего не напечатал — проверять нечего");

        var itself = EngineExitReport.Lines(4242, exitCode, stoppedByPanel: false, At, raw);
        var byPanel = EngineExitReport.Lines(4242, exitCode, stoppedByPanel: true, At, raw);

        var itselfText = string.Join("\n", itself);
        var byPanelText = string.Join("\n", byPanel);

        // КОД ВЫХОДА и PID — факты, без которых запись бесполезна.
        Assert.Contains("4242", itselfText, StringComparison.Ordinal);
        Assert.Contains(Code.ToString(CultureInfo.InvariantCulture), itselfText, StringComparison.Ordinal);

        // ВРЕМЯ — тот самый момент, а не «когда написали».
        Assert.Contains(EngineExitReport.Moment(At), itselfText, StringComparison.Ordinal);
        Assert.Equal("18.10.2026 15:40:19", EngineExitReport.Moment(At));

        // ХВОСТ: обе строки подставного процесса попали в запись.
        Assert.Contains("gotovo", itselfText, StringComparison.Ordinal);
        Assert.Contains("beda", itselfText, StringComparison.Ordinal);

        // КТО ЗАВЕРШИЛ — единственное отличие двух записей, и оно обязано быть.
        Assert.NotEqual(itselfText, byPanelText);

        // И слова этого отличия — те, что названы в словаре: «завершился САМ» против
        // «остановлен панелью». Сверяются по РУССКОМУ словарю явно (`TIn`), иначе проверка
        // зависела бы от языка, выбранного чужим классом в этом же прогоне.
        Assert.Contains("САМ", Loc.TIn("ru", nameof(PanelStrings.SrvEngineExitItselfLogFormat)), StringComparison.Ordinal);
        Assert.Contains("панелью", Loc.TIn("ru", nameof(PanelStrings.SrvEngineExitByPanelLogFormat)), StringComparison.Ordinal);

        // КОДА МОЖЕТ НЕ БЫТЬ — у процесса, которого у панели уже нет дескриптора (гашение
        // по записи после перезапуска панели). Тогда пишутся СЛОВА, а не выдуманный ноль.
        var noCode = string.Join(
            "\n", EngineExitReport.Lines(4242, exitCode: null, stoppedByPanel: true, At, raw));

        Assert.Contains(PanelStrings.SrvEngineExitNoCodeLog, noCode, StringComparison.Ordinal);
        Assert.DoesNotContain(Code.ToString(CultureInfo.InvariantCulture), noCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Журнал_маскирует_токен_в_хвосте()
    {
        const string Secret = "S3CR3T-T0KEN-4711";

        var (_, raw) = RunStub(
            "echo dsh web: http://127.0.0.1:3081/?token=" + Secret + " > nul & "
            + "echo dsh web: http://127.0.0.1:3081/?token=" + Secret + " 1>&2");

        Assert.Contains(raw, line => line.Contains(Secret, StringComparison.Ordinal));

        var text = string.Join("\n", EngineExitReport.Lines(4242, 0, stoppedByPanel: false, At, raw));

        // Маска стоит ВНУТРИ сборки записи: хвост приходит СЫРЫМ, а уходит в файл журнала,
        // который переживёт сессию и попадёт в резервную копию.
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Contains("token=***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Хвост_журнала_держит_последние_сорок_строк()
    {
        var raw = new List<string>();
        for (var i = 1; i <= 45; i++) raw.Add($"L{i:00}");

        var lines = EngineExitReport.Lines(4242, 1, stoppedByPanel: false, At, raw);

        Assert.Equal(EngineExitReport.TailLines + 1, lines.Count);

        var text = string.Join("\n", lines);

        // Последние сорок — это L06…L45: падение движок печатает ПОСЛЕДНИМ, поэтому хвост,
        // а не начало.
        Assert.Contains("L45", text, StringComparison.Ordinal);
        Assert.Contains("L06", text, StringComparison.Ordinal);
        Assert.DoesNotContain("L05", text, StringComparison.Ordinal);
        Assert.DoesNotContain("L01", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ШОВ КОНТРОЛЛЕРА: запись о завершении уходит в ЖУРНАЛ ПАНЕЛИ (тот же приёмник, что и всё
    /// остальное) — а не считается «где-то в отчёте».
    ///
    /// Факты приходят от ПОДСТАВНОГО процесса: настоящий движок панель запускает сама, и своей
    /// командной строки в него не подставить.
    /// </summary>
    [Fact]
    public void Контроллер_пишет_журнал_завершения_в_свой_журнал()
    {
        var root = TempRoots.For("engine-exit-journal");

        try
        {
            Directory.CreateDirectory(root);

            var logged = new List<string>();

            using var controller = new ServerController(
                root,
                () => root,
                line => { lock (logged) logged.Add(line); },
                mayOccupyOwnerPort: false,
                clock: () => At);

            var (exitCode, raw) = RunStub("echo hvost & exit " + Code);

            controller.JournalEngineExit(4242, exitCode, stoppedByPanel: false, tail: raw);

            string[] lines;

            lock (logged) lines = logged.ToArray();

            Assert.False(lines.Length < 2, $"в журнал легло {lines.Length} строк — запись не собралась");

            var head = lines[0];

            Assert.Contains("4242", head, StringComparison.Ordinal);
            Assert.Contains(Code.ToString(CultureInfo.InvariantCulture), head, StringComparison.Ordinal);
            Assert.Contains(EngineExitReport.Moment(At), head, StringComparison.Ordinal);
            Assert.Contains(lines, line => line.Contains("hvost", StringComparison.Ordinal));
        }
        finally
        {
            TempRoots.Remove(root);
        }
    }

    /// <summary>
    /// Подставной процесс: настоящий <c>cmd /c</c>, который печатает в оба потока и выходит
    /// ЗАДАННЫМ кодом. Возвращает код и всё, что напечатал (оба потока вперемешку — так их
    /// и видит панель).
    /// </summary>
    private static (int ExitCode, List<string> Raw) RunStub(string script)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(script);

        var raw = new List<string>();

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (raw) raw.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (raw) raw.Add(e.Data); };

        Assert.True(process.Start(), "подставной процесс не запустился");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Assert.True(process.WaitForExit(30_000), "подставной процесс не завершился за 30 секунд");

        // Хвосты потоков дочитываются ПОСЛЕ выхода: без этого два последних обработчика могут
        // ещё не отработать, и проверка «напечатал ли он что-нибудь» стала бы флаком.
        process.WaitForExit();
        Thread.Sleep(50);

        lock (raw) return (process.ExitCode, raw.ToList());
    }
}
