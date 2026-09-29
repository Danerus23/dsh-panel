using System;
using System.Collections.Generic;
using System.IO;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// Проверки применения темы из ЛЮБОЙ нитки (дефект Д4, живой случай владельца 26.09.2026
/// в 23:10:12).
///
/// Тогда человек нажал «Взять под управление», встраивание шло в ФОНЕ, применение темы упало
/// на «The calling thread cannot access this object because a different thread owns it» —
/// и в журнал легло ЛОЖНОЕ «запомнить согласие не удалось», хотя файл настроек был уже записан
/// и согласие действовало.
///
/// Ядро (<see cref="ThemeApply"/>) не знает ни Avalonia, ни ниток: «где мы» и «как отправить»
/// приходят делегатами. Поэтому обе ветки — «применяем сейчас» и «отправляем туда» — проверяются
/// подставными делегатами, а не запуском второго потока.
/// </summary>
public class ThemeApplyTests
{
    private const string LiveFailure =
        "The calling thread cannot access this object because a different thread owns it.";

    [Fact]
    public void На_нитке_интерфейса_тема_применяется_сразу()
    {
        var applied = new List<PanelTheme>();
        var posted = new List<Action>();
        var failed = new List<Exception>();

        ThemeApply.Apply(() => true, applied.Add, posted.Add, PanelTheme.Dark, failed.Add);

        Assert.Equal(new[] { PanelTheme.Dark }, applied);
        Assert.Empty(posted);
        Assert.Empty(failed);
    }

    [Fact]
    public void Не_на_нитке_тема_отправляется_в_нитку_интерфейса()
    {
        var applied = new List<PanelTheme>();
        var posted = new List<Action>();
        var failed = new List<Exception>();

        ThemeApply.Apply(() => false, applied.Add, posted.Add, PanelTheme.Light, failed.Add);

        // До нитки интерфейса тема не трогается вовсе: именно это и падало 26.09.2026.
        Assert.Empty(applied);
        var action = Assert.Single(posted);

        action();

        Assert.Equal(new[] { PanelTheme.Light }, applied);
        Assert.Empty(failed);
    }

    /// <summary>Сбой применения темы не бросает наружу: сохранение настроек уже состоялось.</summary>
    [Fact]
    public void Сбой_применения_темы_называется_а_не_бросает()
    {
        var failed = new List<Exception>();

        ThemeApply.Apply(() => true, _ => throw new InvalidOperationException(LiveFailure), _ => { }, PanelTheme.Dark, failed.Add);

        var error = Assert.Single(failed);
        var line = ThemeApply.FailedLogLine(error);

        Assert.Contains("тему применить не удалось", line, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.Contains(LiveFailure, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сбой ОТПРАВЛЕННОГО действия ловит само действие: исключение в чужой нитке не поймает уже
    /// никто, и панель упала бы вместе с ним.
    /// </summary>
    [Fact]
    public void Сбой_в_отправленном_действии_тоже_называется()
    {
        var failed = new List<Exception>();
        var posted = new List<Action>();

        ThemeApply.Apply(() => false, _ => throw new InvalidOperationException(LiveFailure), posted.Add, PanelTheme.System, failed.Add);

        Assert.Empty(failed);

        Assert.Single(posted)();

        Assert.Single(failed);
    }

    // ---- сквозная: встраивание в найденный сервер ------------------------------------------

    /// <summary>
    /// Подставная разведка: на порту отвечает DSH, процесс известен и жив.
    /// </summary>
    private static ServerProbe Probe(int port) => new(
        ListenerPid: _ => port == ServerDecisions.OwnerPort ? 19804 : 0,
        Listeners: () => Array.Empty<PortTable.Listener>(),
        CommandLine: _ => string.Empty,
        ProcessName: _ => "node",
        StartTicks: _ => 638_000_000_000_000_000,
        AnswersFingerprint: asked => asked == port);

    /// <summary>
    /// СКВОЗНАЯ ПРОВЕРКА Д4. Встраивание в найденный сервер с настоящими контроллерами:
    /// применение темы падает так же, как падало у владельца (мы «не на нитке интерфейса»),
    /// и согласие обязано быть запомнено ЧЕСТНО — строкой «запомнил согласие» и без ложного
    /// «запомнить согласие не удалось».
    /// </summary>
    [Fact]
    public void Встраивание_запоминает_согласие_даже_когда_тема_падает()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsh-panel-theme-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = AppPaths.Under(dir);
            var log = new List<string>();

            var settings = new SettingsController(
                new SettingsStore(paths.SettingsFile),
                paths,
                allowed: true,
                canReadOwnerEnvironment: false,
                locateEngine: () => null,
                server: () => null,

                // Именно так выглядела живая поломка: сохранение идёт из фоновой нитки,
                // тема уходит «в нитку интерфейса» и падает там.
                applyTheme: theme => ThemeApply.Apply(
                    () => false,
                    _ => throw new InvalidOperationException(LiveFailure),
                    action => action(),
                    theme,
                    error => log.Add(ThemeApply.FailedLogLine(error))),
                log: log.Add);

            var server = new ServerController(
                paths.DshHome,
                () => paths.DataDir,
                log.Add,
                mayOccupyOwnerPort: false,
                probe: Probe(ServerDecisions.OwnerPort),
                adoptedPort: () => settings.Settings.AdoptedServerPort,
                rememberAdoptedPort: port => settings.SetAdoptedServer(port));

            var state = server.Adopt(new FoundServer(ServerDecisions.OwnerPort, 19804, "node"));

            Assert.Equal(ServerPresence.Running, state.Presence);
            Assert.Equal(ServerOwner.Adopted, server.Owner);
            Assert.True(server.ConsentRemembered, "согласие не запомнено — панель спросит снова");

            // Согласие действительно легло В ФАЙЛ — это «спросить один раз» и значит.
            Assert.Equal(ServerDecisions.OwnerPort, settings.Settings.AdoptedServerPort);
            Assert.Equal(ServerDecisions.OwnerPort, new SettingsStore(paths.SettingsFile).Load().Settings.AdoptedServerPort);

            Assert.Contains(log, line => line.Contains("запомнил согласие", StringComparison.Ordinal));
            Assert.DoesNotContain(log, line => line.Contains("запомнить согласие не удалось", StringComparison.Ordinal));

            // И о сбое темы сказано своей строкой — молчания о нём тоже нет.
            Assert.Contains(log, line => line.Contains("тему применить не удалось", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

                var parent = Path.GetDirectoryName(dir);
                if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent);
            }
            catch
            {
                // Родитель занят другой проверкой — это не ошибка.
            }
        }
    }
}
