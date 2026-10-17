using System;
using System.IO;
using System.Linq;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРЕДУПРЕЖДЕНИЕ ПЕРЕД ГАШЕНИЕМ СЕРВЕРА (решение владельца 10.10.2026: *«Важная магия —
/// с предупреждением»*; уточнение к нему: *«„гасит свободно“ значит без нового вопроса, а не без
/// предупреждения»*).
///
/// Проверяется три вещи, и каждая — обещание человека: **что** говорится (порт и что прервётся),
/// **кому** (человеку — сообщением, если оно разрешено; в журнал — всегда) и **когда**
/// (до гашения, иначе это уже не предупреждение, а отчёт).
/// </summary>
public class StopWarningTests
{
    /// <summary>Слова называют ПОРТ и говорят, что прервётся; свой и взятый названы по-разному.</summary>
    [Fact]
    public void Слова_называют_порт_и_что_прервётся()
    {
        var own = StopWarning.Words(own: true, port: 3080);
        var adopted = StopWarning.Words(own: false, port: 3099);

        Assert.Contains("3080", own, StringComparison.Ordinal);
        Assert.Contains("3099", adopted, StringComparison.Ordinal);
        Assert.Contains("работа агента", own, StringComparison.Ordinal);
        Assert.Contains("прервётся", own, StringComparison.Ordinal);
        Assert.NotEqual(own, adopted);

        // Строки — из словарей и без шаблонных скобок на всех трёх языках.
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in new[] { "ServerStopWarningOwnFormat", "ServerStopWarningAdoptedFormat", "NotifyServerStopTitle" })
            {
                var text = Loc.TIn(language, key, 3080);

                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// СКАЗАНО И ЧЕЛОВЕКУ, И В ЖУРНАЛ: сообщение уходит с тем же текстом и своим видом
    /// (<see cref="NoticeKind.ServerStop"/>), а журнал получает строку всегда — даже когда сообщать
    /// некому.
    /// </summary>
    [Fact]
    public void Сказано_и_человеку_и_в_журнал()
    {
        var log = new System.Collections.Generic.List<string>();
        var shown = new System.Collections.Generic.List<(NoticeKind Kind, string Title, string Text)>();

        StopWarning.Say(log.Add, (kind, title, text) => shown.Add((kind, title, text)), own: true, port: 3081);

        Assert.Single(log);
        Assert.Contains("3081", log[0], StringComparison.Ordinal);

        var notice = Assert.Single(shown);

        Assert.Equal(NoticeKind.ServerStop, notice.Kind);
        Assert.Equal(log[0], notice.Text);

        // Сообщать некому (изолированный прогон, проверка) — журнал всё равно получает строку.
        var quiet = new System.Collections.Generic.List<string>();

        StopWarning.Say(quiet.Add, null, own: false, port: 3099);

        Assert.Single(quiet);
        Assert.Contains("3099", quiet[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРЕДУПРЕЖДЕНИЕ СТОИТ ПЕРЕД КАЖДЫМ ГАШЕНИЕМ. Это ворота по исходникам, и они сторожат
    /// ПОРЯДОК: строка «сказать» обязана идти раньше убийства процесса в том же методе. Второй
    /// способ проверки — поднять настоящие контроллеры с подставными убийцами — дорог, а порядок
    /// здесь и есть всё обещание: предупреждение после гашения называется иначе.
    /// </summary>
    [Fact]
    public void Предупреждение_стоит_перед_каждым_гашением()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var path = Path.Combine(root!, "src", "DshPanel", "Server", "ServerController.cs");

        Assert.True(File.Exists(path), "нет исходника контроллера сервера: " + path);

        var lines = File.ReadAllLines(path);
        var kills = 0;
        var warnings = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (line.Contains("StopWarning.Say(", StringComparison.Ordinal)) warnings++;

            var isKill = line.Contains("process.Kill(", StringComparison.Ordinal)
                || line.Contains("_killer.Kill(", StringComparison.Ordinal);

            if (!isKill) continue;

            kills++;

            var said = false;

            // Ищем предупреждение выше, но не дальше начала того же метода. ⚠️ Началом считается
            // и `public`, и `internal`: гашение найденного сервера («поднять свой сервер заново»,
            // решение владельца 13.10.2026) живёт в ПУБЛИЧНОМ методе, и прежний разбор «только
            // private» уходил выше него — к предупреждению СОСЕДНЕГО метода. Нашлось это прогоном:
            // ворота падали на целом дереве, а не на подложенной беде.
            for (var back = index - 1;
                 back >= 0
                 && !lines[back].Contains("private ", StringComparison.Ordinal)
                 && !lines[back].Contains("public ", StringComparison.Ordinal)
                 && !lines[back].Contains("internal ", StringComparison.Ordinal);
                 back--)
            {
                if (!lines[back].Contains("StopWarning.Say(", StringComparison.Ordinal)) continue;

                said = true;
                break;
            }

            Assert.True(said, $"гашение без предупреждения: строка {index + 1} — «{line.Trim()}»");
        }

        // ЧЕТЫРЕ ГАШЕНИЯ, И КАЖДОЕ — СО СВОИМ ПРЕДУПРЕЖДЕНИЕМ: свой процесс, свой по записи,
        // встроенный сервер и найденный («поднять свой сервер заново»).
        Assert.Equal(4, kills);
        Assert.Equal(4, warnings);
    }
}
