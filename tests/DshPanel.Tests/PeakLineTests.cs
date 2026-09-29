using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using DshPanel.Agents;
using DshPanel.Balance;
using DshPanel.Peak;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КОРОТКАЯ СТРОКА ПРО ОКНА ПИКА В ГЛАВНОМ ОКНЕ (слова владельца 27.09.2026): *«Там где написано
/// „окна пика“, проверено и дата с временем нужно… остальная информация не нужна в принципе»*.
///
/// Отсюда четыре обещания, и каждое проверяется своим путём:
///
/// 1. **строка короткая** — «Пики: проверено 24.09.2026»: ни источника (URL), ни расписания,
///    ни дней недели. Прежняя строка была расписанием с источником, и она из главного окна ушла;
/// 2. **форма одна во всех состояниях баланса** — «ключ недоступен», «ответа нет», «ответ есть»
///    показывают ОДНО И ТО ЖЕ: дата проверки таблицы не зависит от того, спросили ли баланс;
/// 3. **дата читаемая** — ISO из профиля приводится к <c>дд.ММ.гггг</c>, а мусор возвращается
///    как есть (ничего не выдумываем и не падаем);
/// 4. **дверь рядом** — «Пики и тарифы…» подписана строкой словаря, доступна всегда и щелчок
///    поднимает РОВНО ОДНУ просьбу; обе кнопки карточки стоят в переносимом ряду и вмещаются
///    в окно на минимальной ширине 640 (дефект Д2: жёсткий ряд однажды перестал бы вмещаться).
/// </summary>
[Collection(PanelLookCollection.Name)]
public class PeakLineTests
{
    /// <summary>Подставной клиент баланса: сети не касается — ответ задаёт проверка.</summary>
    private sealed class FakeClient : IBalanceClient
    {
        public BalanceResult Query(AgentProfile agent, string key, DateTimeOffset now) =>
            new(true, true, 12.34m, "CNY", "12.34 CNY", "пополнено 12.34", string.Empty, now, agent.Id);
    }

    private static BalanceController Controller(
        PanelSettings settings,
        string credentialsPath,
        bool allowed)
    {
        var clock = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

        return new BalanceController(
            new FakeClient(),
            credentialsPath,
            () => settings,
            allowed,
            () => clock,
            action => action(),
            _ => { },
            (_, _, _) => { });
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-panel-peakline-tests", Guid.NewGuid().ToString("N"));

    private static void RemoveTemp(string dir)
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

    /// <summary>Что строка обязана говорить: одна строка словаря и дата из профиля агента.</summary>
    private static string Expected(AgentProfile agent) =>
        string.Format(CultureInfo.CurrentCulture, PanelStrings.PeakCheckedFormat, PeakDecisions.CheckedText(agent));

    // ---------------------------------------------------------------- 1. короткая строка

    /// <summary>
    /// В ГЛАВНОМ ОКНЕ — ТОЛЬКО «ПРОВЕРЕНО КОГДА»: ни источника (URL), ни расписания, ни часов
    /// окон, ни часового пояса. Именно это и просил владелец; полное расписание осталось
    /// в настройках и в отдельном окне «Пики и тарифы».
    /// </summary>
    [AvaloniaFact]
    public void Строка_пиков_короткая_и_без_источника()
    {
        // Прогон без права читать ключ: запроса не будет вовсе, и в окне — честное «не спрашивали».
        var controller = Controller(new PanelSettings(), "нет-такого-файла.yaml", allowed: false);

        var window = new MainWindow();
        window.AttachBalance(controller);
        window.Show();
        PanelTestStand.Settle();

        var text = window.FindControl<TextBlock>("PeakScheduleText")?.Text ?? string.Empty;

        Assert.Equal(Expected(controller.Agent), text);

        // ИСТОЧНИК из главного окна исчез — это и просил владелец.
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AgentCatalog.DeepSeek.PricingSource, text, StringComparison.Ordinal);

        // И перечисления окон больше нет: ни часов расписания, ни часового пояса.
        Assert.DoesNotContain("01:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("06:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", text, StringComparison.Ordinal);

        // Дата показана ЧИТАЕМО: ISO-форма профиля в окно не попадает.
        Assert.DoesNotContain(AgentCatalog.DeepSeek.PricingChecked, text, StringComparison.Ordinal);

        window.Close();
    }

    // ---------------------------------------------------------------- 2. три состояния

    /// <summary>
    /// ФОРМА СТРОКИ ОДНА В ТРЁХ СОСТОЯНИЯХ БАЛАНСА. Дата проверки таблицы цен не зависит от того,
    /// спросили ли баланс: «ключ недоступен», «ответа нет» и «ответ есть» обязаны показывать одно
    /// и то же. Иначе строка про пики мигала бы вместе с балансом, а это про разные вещи.
    ///
    /// ⚠️ Проверка перебора обязана пройти через РАЗНЫЕ состояния: без утверждений о них она была
    /// бы зелёной и на трёх одинаковых контроллерах, то есть ничего не доказывала бы.
    /// </summary>
    [Fact]
    public void Строка_пиков_одинакова_в_трёх_состояниях()
    {
        var dir = TempDir();

        try
        {
            // 1. Ключ недоступен: прогон без права читать ключ владельца.
            var locked = Controller(new PanelSettings(), Path.Combine(dir, "ключ.yaml"), allowed: false);
            Assert.False(locked.Allowed);

            // 2. Ответа нет: права есть, а ключа в файле нет.
            var noAnswer = Controller(new PanelSettings(), Path.Combine(dir, "пусто.yaml"), allowed: true);
            noAnswer.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !noAnswer.Busy, 5000), "запрос баланса не завершился");
            Assert.False(noAnswer.Result.Ok);

            // 3. Ответ есть: ключ на месте, клиент подставной.
            Directory.CreateDirectory(dir);
            var keyPath = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(keyPath, "DEEPSEEK_API_KEY: sk-1234567890abcdef\n", new UTF8Encoding(false));

            var answered = Controller(new PanelSettings(), keyPath, allowed: true);
            answered.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !answered.Busy, 5000), "запрос баланса не завершился");
            Assert.True(answered.Result.Ok);

            // Три состояния ДЕЙСТВИТЕЛЬНО разные — иначе равенство ниже ничего не значит.
            Assert.NotEqual(locked.StatusText, answered.StatusText);

            var expected = Expected(AgentCatalog.DeepSeek);

            Assert.Equal(expected, locked.PeakCheckedText);
            Assert.Equal(expected, noAnswer.PeakCheckedText);
            Assert.Equal(expected, answered.PeakCheckedText);
        }
        finally
        {
            RemoveTemp(dir);
        }
    }

    // ---------------------------------------------------------------- 3. дата

    /// <summary>
    /// ДАТА ПРОВЕРКИ — «дд.ММ.гггг», а неразобранная строка возвращается КАК ЕСТЬ.
    ///
    /// ⚠️ Времени проверки в профиле агента НЕТ: там лежит только дата (<c>PricingChecked</c>,
    /// сегодня ISO <c>2026-09-24</c>). Пример владельца («24.09.2026, 19:40») время содержит,
    /// но панель его не знает — и выдумывать не имеет права. Поэтому проверка сторожит РОВНО дату.
    /// </summary>
    [Fact]
    public void Дата_проверки_как_дд_мм_гггг_а_мусор_как_есть()
    {
        // Живой профиль панели: ISO-дата приводится к читаемому виду.
        Assert.Equal("24.09.2026", PeakDecisions.CheckedText(AgentCatalog.DeepSeek));

        Assert.Equal(
            "01.12.2025",
            PeakDecisions.CheckedText(AgentCatalog.DeepSeek with { PricingChecked = "2025-12-01" }));

        // Разобрать не удалось — возвращаем КАК ЕСТЬ: ни выдуманной даты, ни падения.
        // Профиль правят руками, и мусор в нём — обычное дело, а не повод уронить окно.
        foreach (var garbage in new[] { "не дата", "мусор", "дата проверки", string.Empty })
        {
            Assert.Equal(
                garbage,
                PeakDecisions.CheckedText(AgentCatalog.DeepSeek with { PricingChecked = garbage }));
        }
    }

    // ---------------------------------------------------------------- 4. дверь в пики

    /// <summary>
    /// КНОПКА «ПИКИ И ТАРИФЫ…»: подписана строкой словаря, ВИДНА и ДОСТУПНА ВСЕГДА (таблица
    /// считается из профиля агента — ни ключа, ни сети она не требует), подсказка у неё постоянная,
    /// а щелчок поднимает РОВНО ОДНУ просьбу.
    ///
    /// ⚠️ Доступность проверяется и при НЕДОСТУПНОМ балансе: будь дверь заперта вместе с ключом,
    /// человек не увидел бы расписание ровно тогда, когда оно ему и нужно.
    /// </summary>
    [AvaloniaFact]
    public void Кнопка_пиков_подписана_и_поднимает_ровно_одну_просьбу()
    {
        var window = new MainWindow();

        // Баланс подключён и НЕДОСТУПЕН (прогон без права читать ключ) — дверь обязана остаться.
        window.AttachBalance(Controller(new PanelSettings(), "нет-такого-файла.yaml", allowed: false));

        window.Show();
        PanelTestStand.Settle();

        var button = window.FindControl<Button>("PeakButton");
        Assert.NotNull(button);

        Assert.True(button!.IsVisible, "дверь в окно пиков не показана");
        Assert.True(button.IsEnabled, "дверь в пики обязана быть доступна всегда");
        Assert.Equal(PanelStrings.PeakButton, PanelTestStand.Label(button));
        Assert.Equal(PanelStrings.TipPeakButton, ToolTip.GetTip(button) as string);

        var asked = 0;
        window.PeaksRequested += () => asked++;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, asked);

        // Щелчок поднимает просьбу каждый раз: дверь не «залипает» после первого нажатия.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, asked);

        window.Close();
    }

    // ---------------------------------------------------------------- 5. ряд карточки баланса

    /// <summary>
    /// ОБЕ КНОПКИ КАРТОЧКИ БАЛАНСА — В ОДНОМ ПЕРЕНОСИМОМ РЯДУ. Ряд растёт с каждой новой дверью,
    /// а минимальная ширина окна — 640: жёсткий ряд однажды перестал бы вмещаться (дефект Д2).
    /// </summary>
    [AvaloniaFact]
    public void Кнопки_карточки_баланса_в_переносимом_ряду()
    {
        var window = new MainWindow();

        var refresh = window.FindControl<Button>("RefreshBalanceButton");
        var peaks = window.FindControl<Button>("PeakButton");

        Assert.NotNull(refresh);
        Assert.NotNull(peaks);

        var row = refresh!.Parent as WrapPanel;
        Assert.NotNull(row);
        Assert.Equal(Orientation.Horizontal, row!.Orientation);
        Assert.Same(row, peaks!.Parent);

        window.Close();
    }

    /// <summary>
    /// ОБЕ КНОПКИ ВМЕЩАЮТСЯ В ОКНО НА МИНИМАЛЬНОЙ ШИРИНЕ 640 — по ГЕОМЕТРИИ, а не «нарисовались».
    ///
    /// Дефект Д2 был ровно про это: содержимое переставало вмещаться, и нижние кнопки становились
    /// недостижимы. Мерятся обе крайние точки по X у показанного окна и на двух ширинах: на широкой
    /// «вместилось» выполнялось бы само собой, и снятие переноса прошло бы незамеченным.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(960)]
    [InlineData(640)]
    public void Обе_кнопки_карточки_баланса_вмещаются_на_минимальной_ширине(double width)
    {
        var window = new MainWindow { Width = width, Height = 480 };

        // На экране та же карточка, что видит человек; запроса не будет — прогон без права на ключ.
        window.AttachBalance(Controller(new PanelSettings(), "нет-такого-файла.yaml", allowed: false));

        window.Show();
        PanelTestStand.Settle();

        var scroll = window.FindControl<ScrollViewer>("MainContentScroll");
        Assert.NotNull(scroll);

        scroll!.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        PanelTestStand.Settle();

        foreach (var name in new[] { "RefreshBalanceButton", "PeakButton" })
        {
            var button = window.FindControl<Button>(name);
            Assert.NotNull(button);

            var topLeft = button!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(topLeft);

            var where =
                $"ширина окна {width}, Extent={scroll.Extent.Width:0.#}, Viewport={scroll.Viewport.Width:0.#}, " +
                $"«{name}»: X {topLeft!.Value.X:0.#}, ширина {button.Bounds.Width:0.#}";

            Assert.True(button.IsVisible, $"«{name}» не показана — {where}");
            Assert.True(topLeft.Value.X >= -0.5, $"«{name}» левее видимой области — {where}");
            Assert.True(button.Bounds.Width > 0, $"«{name}» без ширины — {where}");

            Assert.True(
                topLeft.Value.X + button.Bounds.Width <= window.ClientSize.Width + 0.5,
                $"«{name}» не вмещается в окно — {where}");
        }

        window.Close();
    }
}
