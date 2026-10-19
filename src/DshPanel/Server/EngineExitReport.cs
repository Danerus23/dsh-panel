using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ПОСЛЕДНИЕ СЛОВА ДВИЖКА — одной записью в журнал панели, чтобы «упал» и «погасили»
/// различались ПОСЛЕ факта, а не догадкой.
///
/// **От какого случая.** 18.10.2026 в 15:40:19 сервер DSH на 3080 умер, и панель не оставила
/// ни одной улики: кто его завершил, с каким кодом и что он успел сказать перед смертью —
/// неизвестно до сих пор. Вывод движка панель ЗАПОМНИАЛА (кольцевой буфер последних строк),
/// но на завершении не печатала ни кода выхода, ни хвоста, ни того, ГАСИЛА она процесс или он
/// ушёл сам. Три разных события — «человек нажал Остановить» (панель убила свой процесс),
/// «человек нажал Остановить» (панель убила СВОЙ, уже без дескриптора, по записи) и «процесс
/// завершился сам» — выглядели в журнале одинаково или не выглядели вовсе.
///
/// **Что здесь есть и чего нет.** Здесь ТОЛЬКО сборка записи из фактов: PID, код выхода
/// (<c>null</c> — кода нет, например у процесса, которого у панели уже нет дескриптора),
/// кто завершил и хвост вывода. Никакого процесса, никакого времени «сейчас» — момент приходит
/// параметром, поэтому запись проверяется на подставных фактах и на ПОДСТАВНОМ процессе
/// (<c>cmd /c … exit 7</c>), а не на живом сервере.
///
/// ⚠️ **Маска — ЗДЕСЬ, а не у зовущего.** Хвост приходит СЫРЫМИ строками и маскируется внутри
/// (<see cref="EngineLog.Redact"/>): в выводе движка лежит ссылка входа с токеном, а запись
/// уходит в файл журнала, который переживёт сессию и попадёт в резервную копию. Если бы маску
/// ставил зовущий, то новый зовущий однажды забыл бы её — и токен уехал бы в файл.
///
/// **Мутации, которые обязаны упасть:** «код выхода не печатается» · «хвост не маскируется» ·
/// «всегда пишется «остановлен панелью»» · «хвост не ограничен сорока строками».
/// </summary>
public static class EngineExitReport
{
    /// <summary>
    /// Сколько последних строк вывода попадает в запись. Сорок — не «круглое число»: столько же
    /// держит кольцевой буфер контроллера (<see cref="ServerController.RememberedEngineLines"/>),
    /// и запись о завершении показывает ровно то, что панель помнит. Больше — журнал распухнет
    /// на каждом падении; меньше — в хвост не попадёт стек ошибки, который движок печатает
    /// последним.
    /// </summary>
    public const int TailLines = 40;

    /// <summary>Момент для записи: местные дата и время с секундами (журнал читает человек).</summary>
    public static string Moment(DateTimeOffset at) =>
        at.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Запись о завершении процесса движка: первая строка — факты (кто, код, время, сколько строк
    /// хвоста), дальше — хвост, строка за строкой, КАЖДАЯ через маску.
    ///
    /// <paramref name="tail"/> — сырые строки вывода; пустые после маскирования выбрасываются,
    /// берутся последние <see cref="TailLines"/>.
    /// </summary>
    public static IReadOnlyList<string> Lines(
        int pid,
        int? exitCode,
        bool stoppedByPanel,
        DateTimeOffset at,
        IEnumerable<string>? tail)
    {
        var masked = new List<string>();

        if (tail is not null)
        {
            foreach (var line in tail)
            {
                var redacted = EngineLog.Redact(line);
                if (redacted.Length > 0) masked.Add(redacted);
            }
        }

        // Берём ХВОСТ, а не начало: падение движок печатает последним.
        if (masked.Count > TailLines) masked.RemoveRange(0, masked.Count - TailLines);

        var code = exitCode is { } value
            ? value.ToString(CultureInfo.InvariantCulture)
            : PanelStrings.SrvEngineExitNoCodeLog;

        var tailWords = masked.Count == 0
            ? PanelStrings.SrvEngineExitNoTailLog
            : string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineExitTailCountLogFormat, masked.Count);

        var head = string.Format(
            CultureInfo.CurrentCulture,
            stoppedByPanel ? PanelStrings.SrvEngineExitByPanelLogFormat : PanelStrings.SrvEngineExitItselfLogFormat,
            pid,
            code,
            Moment(at),
            tailWords);

        var lines = new List<string>(masked.Count + 1) { head };

        foreach (var line in masked)
            lines.Add(string.Format(CultureInfo.CurrentCulture, PanelStrings.SrvEngineExitTailLineLogFormat, line));

        return lines;
    }
}
