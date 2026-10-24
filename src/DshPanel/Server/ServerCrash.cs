using System;
using System.Collections.Generic;
using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// СВОЙ СЕРВЕР УПАЛ САМ — факты события (DEF-052/053, 2.4.0).
///
/// **От какого случая.** 18.10.2026 сервер DSH на 3080 умер, и панель не сказала об этом
/// человеку: узнать было неоткуда — ни шарика, ни строки «упал сам», ни кода выхода. Журнал
/// получил запись только после отдельной работы над уликами (`EngineExitReport`), и то лишь
/// когда панель заметила, что процесса нет.
///
/// **Что здесь есть.** Только ФАКТЫ падения: номер процесса, код выхода (<c>null</c> — кода нет),
/// момент и хвост вывода, уже прошедший маску (<see cref="EngineLog.Redact"/> делает это в
/// <see cref="EngineExitReport"/>; в событие хвост попадает только маскированным). Никакого
/// процесса и никакого «сейчас» — момент приходит параметром, поэтому событие проверяется на
/// подставных фактах.
/// </summary>
/// <param name="Pid">Номер процесса, который ушёл.</param>
/// <param name="ExitCode">Код выхода; <c>null</c> — кода нет (процесса у панели уже не было).</param>
/// <param name="At">Момент, когда панель увидела уход.</param>
/// <param name="Tail">Последние строки вывода, УЖЕ обезвреженные.</param>
/// <param name="CodeUnknown">
/// Код выхода СПРОСИТЬ НЕ УДАЛОСЬ (DEF-057, 2.4.1): сервер был принят по записи, а дескриптора
/// у панели нет — прав на процесс не дали или он ушёл до подписки. <c>null</c> в <paramref name="ExitCode"/>
/// сам по себе этого не говорит: он значил «кода нет», и человек прочитал бы это как ответ.
/// Флаг отдельный ровно затем, чтобы шарик сказал «неизвестен», а не выдуманный ноль.
/// </param>
public sealed record ServerCrash(
    int Pid,
    int? ExitCode,
    DateTimeOffset At,
    IReadOnlyList<string> Tail,
    bool CodeUnknown = false);

/// <summary>
/// ЧИСТЫЕ РЕШЕНИЯ О ПАДЕНИИ СЕРВЕРА (DEF-052/053, 2.4.0): что считать падением и что сказать
/// человеку шариком.
///
/// ⚠️ **Падение — только СВОЙ сервер и только когда панель его не гасила.** Остановка панелью
/// и остановка человеком идут через <c>ServerController.Stop</c>, выставляют <c>_stoppedByPanel</c>
/// и падением не называются: сказать «сервер упал сам» после «Остановить» значило бы обвинить
/// программу в том, что сделал человек.
///
/// ⚠️ **Авто-подъёма здесь нет и не будет** — решение владельца: панель не поднимает сервер
/// без человека. Поэтому в записи нет ни кнопки, ни поля «поднять»: шарик только сообщает.
/// Стережёт это `ServerCrashTests.Падение_не_поднимает_сервер_само` (счётчик поиска движка
/// остаётся нулём) — мутация «добавить авто» роняет его.
/// </summary>
public static class ServerCrashDecisions
{
    /// <summary>
    /// Падение ли это: свой процесс и НЕ по слову панели (человек нажал «Остановить» — это не падение).
    /// </summary>
    public static bool IsCrash(bool ours, bool stoppedByPanel) => ours && !stoppedByPanel;

    /// <summary>
    /// Тело шарика: код выхода и время. Слово «сам» живёт в заголовке
    /// (<see cref="PanelStrings.NotifyServerCrashedTitle"/>), а не дублируется в теле.
    ///
    /// ⚠️ **ТРИ РАЗНЫХ ОТВЕТА, И ПУТАТЬ ИХ НЕЛЬЗЯ (DEF-057):** код ЕСТЬ · кода НЕТ (у панели не было
    /// дескриптора вовсе) · код СПРОСИТЬ НЕ УДАЛОСЬ (<see cref="ServerCrash.CodeUnknown"/>). Третий
    /// говорит «неизвестен», а не подставляет ноль: ноль — это тоже код, и человек прочитал бы его
    /// как «вышел чисто».
    /// </summary>
    public static string NoticeText(ServerCrash crash) =>
        string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.NotifyServerCrashedFormat,
            crash.ExitCode is { } code
                ? code.ToString(CultureInfo.InvariantCulture)
                : crash.CodeUnknown
                    ? PanelStrings.SrvEngineExitCodeUnknownLog
                    : PanelStrings.SrvEngineExitNoCodeLog,
            EngineExitReport.Moment(crash.At));

    /// <summary>
    /// Хвост, каким он уходит в событие: каждая строка через маску, пустые выбрасываются.
    ///
    /// ⚠️ Маска — ЗДЕСЬ, а не у получателя события: подписчиков может стать больше одного
    /// (шарик, отчёт, будущий экран), и каждый забывший маску вынес бы токен входа наружу.
    /// </summary>
    public static IReadOnlyList<string> MaskedTail(IEnumerable<string>? tail)
    {
        var masked = new List<string>();

        if (tail is null) return masked;

        foreach (var line in tail)
        {
            var redacted = EngineLog.Redact(line);
            if (redacted.Length > 0) masked.Add(redacted);
        }

        return masked;
    }
}
