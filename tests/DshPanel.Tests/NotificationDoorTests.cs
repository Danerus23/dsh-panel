using System;
using System.Collections.Generic;
using System.ComponentModel;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВЕРЬ УВЕДОМЛЕНИЙ (2.2.1): исключение из показа сообщения не уходит наверх, причина пишется
/// в журнал, а строка «показано» появляется ТОЛЬКО после успешного показа.
///
/// **От какого случая.** В 2.2.0 <c>App.Notify</c> звал <c>TrayIconHost.ShowNotification</c> без
/// защиты. Та бросает `InvalidOperationException`, когда значок не поставлен, и `Win32Exception`,
/// когда оболочка отказала, — а приходило это на нитку интерфейса из `ContinueWith` фоновой копии.
/// Глобальных обработчиков в панели нет (0 мест), поэтому процесс умирал с `0xe0434352`
/// (разбор и стек: <c>_team\220\defekt-start-crash.md</c>).
///
/// **Что доказывают эти проверки.** Что дверь держит ЛЮБОЕ исключение (не два названных типа), что
/// причина попадает в журнал вместе с заголовком сообщения, что успешный показ пишет строку о показе,
/// что эта строка идёт ПОСЛЕ показа, и что даже отказ самого журнала панель не роняет.
///
/// **Мутации, которые валят этот класс** (сняты прогоном):
/// * «убрать `try/catch` из `Announce`» → красные `Исключение_из_показа_не_уходит_вверх_и_называется_в_журнале`,
///   `Дверь_держит_любое_исключение` (×4), `После_отказа_показ_не_повторяется`, `Отказ_журнала_не_ломает_дверь`;
/// * «писать строку о показе ДО показа» → красная `Строка_о_показе_пишется_после_показа`.
/// </summary>
public class NotificationDoorTests
{
    /// <summary>
    /// Начало строк берётся ИЗ ПРОДУКТА, а не литералом: так проверка не разойдётся с текстом
    /// словаря и не станет проверять собственную догадку.
    /// </summary>
    private static string Prefix(string format) => format.Split('{')[0];

    private static string FailedPrefix() => Prefix(PanelStrings.NotifyFailedLogFormat);

    private static string ShownPrefix() => Prefix(PanelStrings.NotifyShownLogFormat);

    [Fact]
    public void Исключение_из_показа_не_уходит_вверх_и_называется_в_журнале()
    {
        var lines = new List<string>();
        var thrown = new InvalidOperationException("значок ещё не поставлен: сначала Show()");

        var shown = NotificationDoor.Announce(() => throw thrown, lines.Add, "Копия не снята");

        Assert.False(shown);

        // РОВНО ОДНА строка и это строка отказа: строки «показано» при отказе быть не должно.
        var line = Assert.Single(lines);
        Assert.True(line.StartsWith(FailedPrefix(), StringComparison.Ordinal), "строка журнала не начинается со строки отказа: " + line);
        Assert.True(line.Contains("Копия не снята", StringComparison.Ordinal), "в строке нет заголовка сообщения: " + line);
        Assert.True(line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal), "в строке нет типа исключения: " + line);
        Assert.True(line.Contains("значок ещё не поставлен", StringComparison.Ordinal), "в строке нет текста исключения: " + line);
    }

    /// <summary>
    /// Дверь обязана держать ЛЮБОЕ исключение показа, а не два известных типа: иначе следующее
    /// новое исключение снова уронит панель — то есть защита была бы точечной, а не дверью.
    /// </summary>
    [Theory]
    [InlineData("значок ещё не поставлен")]
    [InlineData("оболочка отказалась показать уведомление")]
    [InlineData("совсем новое исключение, которого раньше не было")]
    [InlineData("")]
    public void Дверь_держит_любое_исключение(string message)
    {
        var lines = new List<string>();
        Exception thrown = message.Length == 0
            ? new InvalidOperationException()
            : new Win32Exception(5, message);

        var shown = NotificationDoor.Announce(() => throw thrown, lines.Add, "Низкий баланс");

        Assert.False(shown);
        var line = Assert.Single(lines);
        Assert.True(line.Contains("Низкий баланс", StringComparison.Ordinal), "в строке нет заголовка: " + line);
    }

    [Fact]
    public void Показанное_сообщение_пишет_строку_о_показе()
    {
        var lines = new List<string>();
        var called = 0;

        var shown = NotificationDoor.Announce(() => called++, lines.Add, "Копия снята на ходу");

        Assert.True(shown);
        Assert.Equal(1, called);

        var line = Assert.Single(lines);
        Assert.True(line.StartsWith(ShownPrefix(), StringComparison.Ordinal), "строка журнала не начинается со строки показа: " + line);
        Assert.True(line.Contains("Копия снята на ходу", StringComparison.Ordinal), "в строке нет заголовка: " + line);
    }

    /// <summary>
    /// ⚠️ ПОРЯДОК: строка «показано» пишется ПОСЛЕ показа. До 2.2.1 она писалась ДО вызова — и
    /// в разборе падения журнал утверждал «показано» за миллисекунды до смерти процесса.
    /// Проверка смотрит на последовательность записей, а не на их наличие: порядок — это и есть предмет.
    /// </summary>
    [Fact]
    public void Строка_о_показе_пишется_после_показа()
    {
        var order = new List<string>();

        var shown = NotificationDoor.Announce(() => order.Add("показ"), order.Add, "Копия снята на ходу");

        Assert.True(shown);
        Assert.Equal(2, order.Count);
        Assert.Equal("показ", order[0]);
        Assert.True(order[1].StartsWith(ShownPrefix(), StringComparison.Ordinal), "вторая запись — не строка о показе: " + order[1]);
    }

    /// <summary>
    /// Журнал может отказать сам (диск, права, закрытый файл). Дверь обязана остаться дверью:
    /// исключение из журнала тоже не уходит наверх, иначе панель умирала бы на записи строки
    /// о том, что она не смогла показать сообщение.
    /// </summary>
    [Fact]
    public void Отказ_журнала_не_ломает_дверь()
    {
        var shown = NotificationDoor.Announce(
            () => throw new InvalidOperationException("значка нет"),
            _ => throw new InvalidOperationException("журнал не принял строку"),
            "Копия не снята");

        Assert.False(shown);
    }

    /// <summary>
    /// Показ зовётся РОВНО ОДИН раз: повтор после отказа был бы новым шансом уронить панель и показал
    /// бы человеку два сообщения вместо одного.
    /// </summary>
    [Fact]
    public void После_отказа_показ_не_повторяется()
    {
        var calls = 0;
        var lines = new List<string>();

        Assert.False(NotificationDoor.Announce(() => { calls++; throw new InvalidOperationException("нет значка"); }, lines.Add, "Копия не снята"));
        Assert.Equal(1, calls);
        Assert.Single(lines);
    }
}
