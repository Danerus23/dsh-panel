using System;
using System.Collections.Generic;
using DshPanel.Isolation;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОТКРЫТИЕ РАБОТЫ АГЕНТА В БРАУЗЕРЕ — одна дверь на кнопку главного окна и на пункт меню значка.
///
/// Главное, что здесь доказывается, — **в прогоне проверки браузер не открывается НИКОГДА,
/// даже когда ссылка есть**. Проверяется это швом (<c>opener</c>): настоящий запуск открыл бы окно
/// на рабочем столе владельца, поэтому проверка подставляет счётчик и требует, чтобы его
/// НЕ ТРОНУЛИ.
///
/// ⚠️ Право приходит в дверь ОТКРЫТЫМ параметром (<see cref="RunRights.MayOpenBrowser"/>) — так
/// и ловится находка холодной проверки 27.09.2026: <c>--shell-selftest</c> идёт обычным путём
/// панели и НЕ изолирован, и по одному лишь признаку изолированности он имел право открыть браузер.
/// </summary>
public class AgentBrowserTests
{
    private const string Url = "http://127.0.0.1:3080/?token=SECRET-TOKEN";

    private sealed class Opener
    {
        public List<string> Calls { get; } = new();
        public string Result { get; set; } = string.Empty;

        public string Open(string url)
        {
            Calls.Add(url);
            return Result;
        }
    }

    /// <summary>
    /// Красная линия 8: прогон проверки не показывает владельцу ничего. Значок и окно у него
    /// закрыты, а браузер — тоже: это такое же окно на его рабочем столе.
    ///
    /// ⚠️ Ссылка здесь НЕПУСТАЯ намеренно. Пустой ссылкой предохранитель проверялся бы
    /// случайностью: панель не открыла бы браузер просто потому, что открывать нечего, и «дверь
    /// закрыта» осталось бы непроверенным (ровно на этом и попалась холодная проверка 27.09.2026).
    /// </summary>
    [Fact]
    public void В_прогоне_проверки_браузер_не_открывается_и_шов_не_зовут()
    {
        var opener = new Opener();
        var log = new List<string>();

        var opened = AgentBrowser.TryOpen(
            mayOpenBrowser: false, Url, opener.Open, log.Add, out var error);

        Assert.False(opened);
        Assert.Empty(opener.Calls);
        Assert.Contains(log, line => line.Contains("прогон проверки", StringComparison.Ordinal));

        // Ошибка не пустая: вызывающий обязан сказать человеку, почему ничего не открылось.
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// ПРАВО считается у <see cref="RunRights"/>, и считается оно ПРАВИЛЬНО на всех четырёх
    /// сочетаниях. Здесь же видно то, чего не хватало до 27.09.2026: **прогон проверки без корня
    /// (это и есть <c>--shell-selftest</c>) права НЕ имеет**, хотя и не изолирован.
    /// </summary>
    [Fact]
    public void Право_открыть_браузер_есть_только_у_обычного_запуска_человеком()
    {
        var cases = new (bool Isolated, bool Human, bool Expected)[]
        {
            (false, true,  true),
            (false, false, false),   // прогон проверки без корня — в том числе --shell-selftest
            (true,  true,  false),
            (true,  false, false),
        };

        Assert.Equal(4, cases.Length);

        foreach (var (isolatedRun, human, expected) in cases)
        {
            var context = RunContext.Create(
                isolatedRun
                    ? RunRequest.Parse(new[] { RunRequest.RootSwitchArgument, TempRoot() }, NoEnv)
                    : RunRequest.Owner());

            Assert.Equal(expected, RunRights.MayOpenBrowser(context, human));
        }
    }

    /// <summary>
    /// Обычный запуск человеком: ссылка есть — открывается РОВНО один раз, и ровно она.
    /// Ни в журнал, ни в сообщение сама ссылка при этом не попадает: там только «открыто».
    /// </summary>
    [Fact]
    public void У_человека_ссылка_открывается_один_раз_и_в_журнал_не_попадает()
    {
        var opener = new Opener();
        var log = new List<string>();

        var opened = AgentBrowser.TryOpen(
            mayOpenBrowser: true, Url, opener.Open, log.Add, out var error);

        Assert.True(opened);
        Assert.Equal(string.Empty, error);
        Assert.Equal(new[] { Url }, opener.Calls);

        Assert.Contains(log, line => line.Contains("по щелчку человека", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("SECRET-TOKEN", StringComparison.Ordinal));
    }

    /// <summary>
    /// Пустая ссылка — «нечем открывать», и это НЕ то же самое, что «открылось»: панель говорит
    /// об этом словами, а шов не зовут вовсе.
    /// </summary>
    [Fact]
    public void Без_ссылки_открывать_нечем()
    {
        var opener = new Opener();
        var log = new List<string>();

        foreach (var none in new[] { string.Empty, "   ", null })
        {
            var opened = AgentBrowser.TryOpen(
                mayOpenBrowser: true, none, opener.Open, log.Add, out var error);

            Assert.False(opened);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        Assert.Empty(opener.Calls);
    }

    /// <summary>
    /// Отказ оболочки (нет браузера по умолчанию — обычное дело на чистой машине) не роняет
    /// панель: причина уходит и в журнал, и вызывающему, а панель живёт дальше.
    /// </summary>
    [Fact]
    public void Отказ_оболочки_называется_словами_и_не_роняет_панель()
    {
        var opener = new Opener { Result = "Win32Exception — нет приложения для http" };
        var log = new List<string>();

        var opened = AgentBrowser.TryOpen(
            mayOpenBrowser: true, Url, opener.Open, log.Add, out var error);

        Assert.False(opened);
        Assert.Contains("нет приложения", error, StringComparison.Ordinal);
        Assert.Contains(log, line => line.Contains("открыть агента в браузере не удалось", StringComparison.Ordinal));

        // Исключение из шва тоже не роняет: оно превращается в ту же строку отказа.
        var throwing = AgentBrowser.TryOpen(
            mayOpenBrowser: true,
            Url,
            _ => throw new InvalidOperationException("шов упал"),
            log.Add,
            out var thrown);

        Assert.False(throwing);
        Assert.Contains("шов упал", thrown, StringComparison.Ordinal);
    }

    private static Func<string, string?> NoEnv => _ => null;

    private static string TempRoot() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-panel-browser", Guid.NewGuid().ToString("N"));
}
