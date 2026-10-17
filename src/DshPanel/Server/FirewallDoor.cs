using System.ComponentModel;
using System.Diagnostics;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>
/// ЧЕМ КОНЧИЛОСЬ ИЗМЕНЕНИЕ ПРАВИЛА: сделано · человек отказал в повышении · не вышло (слова).
///
/// ⚠️ Три состояния, а не два, и это из случая: отказ в запросе Windows — НЕ беда панели и не
/// «ничего не произошло само». Человеку надо сказать ровно это («подтверждение не получено,
/// ничего не изменено»), иначе он будет искать поломку там, где сам нажал «Нет».
/// </summary>
public sealed record FirewallChange(bool Done, bool Refused, string Text)
{
    /// <summary>Сделано. <paramref name="text"/> — слова человеку; проверка чтением идёт после (<see cref="FirewallWork"/>).</summary>
    public static FirewallChange Yes(string text) => new(true, false, text);

    /// <summary>Не вышло, и слова об этом уже собраны.</summary>
    public static FirewallChange No(string text) => new(false, false, text);

    /// <summary>Человек не дал подтверждения Windows — правило не появилось и не пропало.</summary>
    public static FirewallChange Refusal() => new(false, true, FirewallDecisions.Refused());
}

/// <summary>
/// ДВЕРЬ БРАНДМАУЭРА: ЧТЕНИЕ — БЕЗ ПРАВ, ИЗМЕНЕНИЕ — С ПОВЫШЕНИЕМ (этап F4, кусок 1).
///
/// **Зачем дверь.** Ровно затем же, зачем она у калитки, копий и обновления: решение о том,
/// ЧТО и КАК менять, живёт в <see cref="FirewallDecisions"/> и проверяется без прав, а настоящее
/// исполнение (<c>netsh advfirewall</c> с запросом Windows) — здесь и только здесь. Проверки
/// подставляют свою дверь и перебирают состояния, не трогая машину владельца: <c>netsh … add</c>
/// в прогоне проверки не зовётся НИ РАЗУ.
///
/// ⚠️ **ЧТЕНИЕ ПРАВ НЕ ТРЕБУЕТ** и потому доступно всегда: «правило есть или его нет» — это
/// вопрос о состоянии машины, а не изменение. Изменение же спрашивает право
/// (<see cref="RunRights.MayChangeFirewall"/>) и, если права нет, отвечает отказом СЛОВАМИ —
/// вторая линия обороны к тому, что настоящая дверь и не создаётся у прогона проверки.
///
/// ⚠️ **Ни одна команда не приходит сюда строкой ИЗ ОКНА.** Дверь получает порт, а строку собирает
/// <see cref="FirewallDecisions"/> — единственное место, где вообще написано, каким правилом
/// панель пользуется.
/// </summary>
public interface IFirewallDoor
{
    /// <summary>Что с нашим правилом сейчас. Чтение — без прав.</summary>
    FirewallState Read(int port);

    /// <summary>Поставить узкое правило на этот порт. Возвращает, ЧТО СКАЗАТЬ (проверка — в <see cref="FirewallWork"/>).</summary>
    FirewallChange Add(int port);

    /// <summary>Убрать СВОЁ правило (по имени панели).</summary>
    FirewallChange Remove();
}

/// <summary>
/// НАСТОЯЩАЯ ДВЕРЬ: <c>netsh advfirewall</c>, чтение — обычным запуском, изменение — с повышением
/// (<c>Verb = "runas"</c>, то есть запрос Windows).
///
/// **Что именно выполняется и с каким повышением** (владелец спрашивал прямо):
///
/// * чтение: <c>%SystemRoot%\System32\netsh.exe advfirewall firewall show rule name="DshPanel2-Phone-Gate" …</c>
///   — БЕЗ повышения, <c>UseShellExecute = false</c>, вывод перехватывается;
/// * изменение: тот же <c>netsh.exe</c> с <c>advfirewall firewall add|delete rule …</c>
///   и <c>Verb = "runas"</c> — Windows показывает запрос подтверждения, и БЕЗ него не выполняется
///   ничего. Панель при этом сама НЕ перезапускается с правами: повышается только <c>netsh</c>.
/// * **отказ человека** (<c>ERROR_CANCELLED 1223</c>) — это <see cref="FirewallChange.Refused"/>:
///   правило не появляется (и не пропадает), а человеку говорится словами, что подтверждение
///   не получено и ничего не изменено.
///
/// ⚠️ **Путь к <c>netsh</c> берётся из System32, а не из PATH** (<see cref="NetshPath"/>): запуск
/// с повышением по имени из PATH — это запуск с правами того, кого первым найдёт PATH.
/// </summary>
public sealed class ElevatedFirewallDoor : IFirewallDoor
{
    private readonly string _exePath;
    private readonly bool _humanLaunch;
    private readonly RunContext _context;
    private readonly Action<string> _log;

    public ElevatedFirewallDoor(string exePath, bool humanLaunch, RunContext context, Action<string> log)
    {
        _exePath = exePath ?? string.Empty;
        _humanLaunch = humanLaunch;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// ПУТЬ К <c>netsh</c> — ТОЛЬКО ИЗ СИСТЕМНОГО КАТАЛОГА. Отдельным членом, чтобы это было
    /// видно и проверяемо: «взяли из PATH» здесь означало бы запуск с правами чужой программы.
    /// </summary>
    public static string NetshPath() =>
        Path.Combine(Environment.SystemDirectory, FirewallDecisions.NetshFile);

    public FirewallState Read(int port) =>
        FirewallDecisions.Read(
            Show(FirewallDecisions.ShowByName()),
            Show(FirewallDecisions.ShowNarrow()),
            port,
            _exePath);

    public FirewallChange Add(int port)
    {
        if (!Allowed()) return FirewallChange.No(Loc.T(PanelStrings.FirewallNoDoor));

        var command = FirewallDecisions.Add(port, _exePath);

        if (!command.Ok) return FirewallChange.No(command.Refusal);

        var run = Elevate(command.Line);

        if (run.Kind == FirewallElevation.Refused)
        {
            _log(Loc.T(PanelStrings.FirewallRefusedLog));

            return FirewallChange.Refusal();
        }

        if (run.Kind != FirewallElevation.Done) return FirewallChange.No(FirewallDecisions.Failed(run.Reason));

        _log(string.Format(
            System.Globalization.CultureInfo.CurrentCulture, PanelStrings.FirewallAddedLogFormat, port));

        // ПУСТЫЕ СЛОВА НАМЕРЕННО: «правило добавлено» скажет <see cref="FirewallWork"/> — и только
        // после того, как ПРОЧТЁТ состояние. Код возврата netsh успехом не считается.
        return FirewallChange.Yes(string.Empty);
    }

    public FirewallChange Remove()
    {
        if (!Allowed()) return FirewallChange.No(Loc.T(PanelStrings.FirewallNoDoor));

        var run = Elevate(FirewallDecisions.Remove());

        if (run.Kind == FirewallElevation.Refused)
        {
            _log(Loc.T(PanelStrings.FirewallRefusedLog));

            return FirewallChange.Refusal();
        }

        if (run.Kind != FirewallElevation.Done) return FirewallChange.No(FirewallDecisions.Failed(run.Reason));

        _log(Loc.T(PanelStrings.FirewallRemovedLog));

        return FirewallChange.Yes(string.Empty);
    }

    /// <summary>
    /// ПРАВО: менять правила машины может только обычный запуск панели человеком. Спрашивается
    /// ВНУТРИ изменения (а не только там, где дверь создаётся) — ровно как калитка спрашивает своё
    /// право внутри <c>PhoneGate.Start</c>: забыть выдать право безопасно, забыть запретить — нет.
    /// </summary>
    private bool Allowed()
    {
        if (RunRights.MayChangeFirewall(_context, _humanLaunch)) return true;

        _log(Loc.T(PanelStrings.FirewallNoRightLog));

        return false;
    }

    /// <summary>ЧТЕНИЕ БЕЗ ПОВЫШЕНИЯ: обычный запуск, вывод перехвачен, окна нет.</summary>
    private static FirewallAnswer Show(string line)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = NetshPath(),
                Arguments = line,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null) return new FirewallAnswer(-1, string.Empty);

            var output = process.StandardOutput.ReadToEnd();

            process.WaitForExit();

            return new FirewallAnswer(process.ExitCode, output);
        }
        catch (Exception)
        {
            // netsh не запустился вовсе (нет файла, отказано в доступе) — «прочитать не удалось»,
            // и разбор скажет это словами. Молчание здесь выдало бы беду за «правила нет».
            return new FirewallAnswer(-1, string.Empty);
        }
    }

    /// <summary>
    /// ИЗМЕНЕНИЕ С ПОВЫШЕНИЕМ. Панель показывает запрос Windows и ждёт: <c>runas</c> — это и есть
    /// подтверждение человека, поэтому окно панели на это время занято (кнопка выключена, сказано
    /// «ждём подтверждения»). Вывод не перехватывается: с <c>runas</c> его и нельзя перехватить,
    /// и он не нужен — успех подтверждается ЧТЕНИЕМ после изменения (<see cref="FirewallWork"/>).
    ///
    /// ⚠️ Исход решает <see cref="FirewallDecisions.Elevated"/> — по НОМЕРУ ошибки, а не по
    /// «не ноль»: иначе «человек нажал Нет» и «netsh сломался» сказались бы одинаково.
    /// </summary>
    private static (FirewallElevation Kind, string Reason) Elevate(string line)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = NetshPath(),
                Arguments = line,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null) return (FirewallElevation.Failed, "netsh: not started");

            process.WaitForExit();

            var kind = FirewallDecisions.Elevated(process.ExitCode, 0);

            return (
                kind,
                kind == FirewallElevation.Done ? string.Empty : "netsh: exit " + process.ExitCode);
        }
        catch (Win32Exception failed)
        {
            var kind = FirewallDecisions.Elevated(-1, failed.NativeErrorCode);

            return (
                kind,
                kind == FirewallElevation.Refused ? string.Empty : "netsh: error " + failed.NativeErrorCode);
        }
        catch (Exception failed)
        {
            // Текст исключения наружу НЕ отдаётся: в нём бывают пути машины. Названо только то,
            // что это не Win32-беда, — а подробность человеку здесь и не нужна.
            return (FirewallElevation.Failed, "netsh: " + failed.GetType().Name);
        }
    }
}
