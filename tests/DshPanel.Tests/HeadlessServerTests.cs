using System;
using DshPanel.Headless;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗВЕДКА СЕРВЕРА для режимов без окна: один вопрос и только чтение.
///
/// Проверяется здесь не «умеет ли панель находить DSH» — это делает `DiscoveryTests`, — а ГРАНИЦА
/// вопроса: спрашивают ровно про порт ЭТОГО прогона. Без неё `--restore` отказывал бы на всякой
/// машине, где у человека что-то запущено (у него на 3080 стоит живой DSH), то есть режим
/// не работал бы никогда. Гашения в разведке нет вовсе: она читает таблицу портов, командную
/// строку процесса и отпечаток — и ничего больше.
/// </summary>
public class HeadlessServerTests
{
    /// <summary>Командная строка движка — та же форма, что у живого сервера (`docs\ENGINE.md` §5).</summary>
    private static string DshCommandLine(int port) =>
        "\"C:\\Program Files\\nodejs\\node.exe\" " +
        "C:\\Users\\Public\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js " +
        $"web --no-open --port {port}";

    /// <summary>
    /// Подставная дверь к системе: слушающие порты, командная строка и отпечаток задаются здесь.
    /// Настоящая разведка при этом работает НАСТОЯЩАЯ — подменяется только источник фактов.
    ///
    /// Имя процесса — «node», а не «node.exe»: система отдаёт его БЕЗ расширения, и примета
    /// владения считает «node.exe» чужим процессом (`ProcessFacts.IsNodeImage`) — так она и задумана.
    /// </summary>
    private static ServerProbe Probe(
        PortTable.Listener[]? listeners,
        string? commandLine = null,
        Func<int, bool>? fingerprint = null,
        string processName = "node") =>
        new(
            ListenerPid: port => listeners is null
                ? -1
                : Array.Find(listeners, listener => listener.Port == port).Pid,
            Listeners: () => listeners,
            CommandLine: _ => commandLine ?? string.Empty,
            ProcessName: _ => processName,
            StartTicks: _ => 1,
            AnswersFingerprint: fingerprint ?? (_ => true));

    private static PortTable.Listener[] Listening(params (int Port, int Pid)[] rows)
    {
        var listeners = new PortTable.Listener[rows.Length];
        for (var i = 0; i < rows.Length; i++) listeners[i] = new PortTable.Listener(rows[i].Port, rows[i].Pid);

        return listeners;
    }

    /// <summary>
    /// ГЛАВНАЯ проверка файла. На порту владельца (3080) работает его живой DSH, а порт ЭТОГО
    /// прогона (3081) свободен. Разведка обязана ответить «не отвечает»: чужой сервер к данным
    /// изолированного прогона отношения не имеет, и отказывать из-за него нельзя.
    /// </summary>
    [Fact]
    public void Разведка_смотрит_только_порт_этого_прогона()
    {
        var ownerOnly = Probe(Listening((3080, 4242)), DshCommandLine(3080), _ => true);

        var facts = HeadlessServer.Read(ownerOnly, 3081);

        Assert.Equal(ServerAnswer.Idle, facts.Answer);
        Assert.Equal(3081, facts.Port);
        Assert.Equal(0, facts.Pid);

        // И обратная сторона той же границы: на СВОЁМ порту сервер виден — иначе проверка выше
        // зеленела бы и у разведки, которая не смотрит никуда.
        var ours = HeadlessServer.Read(
            Probe(Listening((3081, 5150)), DshCommandLine(3081), _ => true), 3081);

        Assert.Equal(ServerAnswer.Working, ours.Answer);
        Assert.Equal(5150, ours.Pid);
        Assert.Equal(3081, ours.Port);
    }

    /// <summary>
    /// Опознание — по приметам владения, а не по «кто-то слушает порт»: чужая программа на нашем
    /// порту это НЕ сервер DSH, и накат из-за неё отказывать не должен.
    /// </summary>
    [Fact]
    public void Чужая_программа_на_нашем_порту_это_не_сервер()
    {
        // Отпечаток не сошёлся: слушают, но это не DSH.
        var noAnswer = Probe(Listening((3081, 777)), DshCommandLine(3081), _ => false);
        Assert.Equal(ServerAnswer.Idle, HeadlessServer.Read(noAnswer, 3081).Answer);

        // Отпечаток сошёлся, а командная строка не наша — тоже не сервер.
        var alien = Probe(
            Listening((3081, 777)),
            "\"C:\\Program Files\\nodejs\\node.exe\" server.js --port 3081",
            _ => true);

        Assert.Equal(ServerAnswer.Idle, HeadlessServer.Read(alien, 3081).Answer);

        // И порт, объявленный в командной строке, обязан совпасть со слушающим: «слушает один,
        // а объявлен другой» — противоречие, а не наш сервер.
        var mismatch = Probe(Listening((3081, 777)), DshCommandLine(9999), _ => true);
        Assert.Equal(ServerAnswer.Idle, HeadlessServer.Read(mismatch, 3081).Answer);
    }

    /// <summary>
    /// «Посмотреть не удалось» — ОТДЕЛЬНЫЙ ответ, а не «не работает»: молчание таблицы портов
    /// это не молчание сервера, и в отчёте об этом говорится словами.
    /// </summary>
    [Fact]
    public void Нечитаемая_таблица_портов_это_не_не_работает()
    {
        var blind = HeadlessServer.Read(Probe(listeners: null), 3081);

        Assert.Equal(ServerAnswer.Unknown, blind.Answer);
        Assert.NotEqual(ServerAnswer.Idle, blind.Answer);
        Assert.Equal(3081, blind.Port);

        // Порта нет вовсе (так выглядит неразобранная настройка) — спрашивать нечего.
        Assert.Equal(ServerAnswer.Idle, HeadlessServer.Read(Probe(listeners: null), 0).Answer);
    }
}
