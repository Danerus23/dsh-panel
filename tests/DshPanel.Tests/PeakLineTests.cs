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
using DshPanel.Localization;
using DshPanel.Peak;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ДВЕ ДАТЫ ПРОВЕРКИ ЦЕН В ГЛАВНОМ ОКНЕ (слова владельца 27.09.2026 и 20.10.2026, F-5).
///
/// Слова 27.09.2026: *«Там где написано „окна пика“, проверено и дата с временем нужно… остальная
/// информация не нужна в принципе»*. Слова 20.10.2026 (пункт 4 пакета 2.3.1, вариант «б»):
/// карточка показывала дату из ПРОФИЛЯ движка (24.09.2026), а панель с тех пор разбирает цены
/// САМА ежедневно — человек читал чужую дату как дату своей проверки.
///
/// Отсюда четыре обещания, и каждое проверяется своим путём:
///
/// 1. **в карточке ДВЕ даты двумя разными строками**: своя — когда цены разбирала панель
///    (`PanelSettings.PricingCheckedAt`, формат <c>дд.ММ.гггг</c>), и профильная — ОТДЕЛЬНОЙ
///    строкой с подписью источника («дата из профиля сервера DSH: …»). Своей отметки нет — честное
///    «не проверяли», а не подставленная профильная;
/// 2. **форма одна во всех состояниях баланса** — «ключ недоступен», «ответа нет», «ответ есть»
///    показывают ОДНО И ТО ЖЕ: даты проверки цен не зависят от того, спросили ли баланс;
/// 3. **даты читаемые** — ISO из профиля приводится к <c>дд.ММ.гггг</c>, мусор возвращается
///    как есть (ничего не выдумываем и не падаем); своя отметка без даты — пусто;
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

    /// <summary>Уборка только СВОЕГО каталога: общий родитель семейства не снимается (t3, `TempRoots`).</summary>
    private static void RemoveTemp(string dir) => TempRoots.Remove(dir);

    /// <summary>
    /// Отметка «панель сама разбирала цены» — МЕСТНЫЙ полдень: дата одна в любом часовом поясе
    /// (полночь соседнего пояса дала бы вчерашний день и проверку, зависящую от машины).
    /// </summary>
    private static string OwnCheckedStamp() =>
        PricingDecisions.Stamp(new DateTimeOffset(DateTime.Today.AddHours(12)));

    /// <summary>Своя дата проверки в читаемом виде — как её покажет окно.</summary>
    private static string OwnDate() => DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

    /// <summary>Своя строка: «Пики: проверено {дата}» или честное «Пики: не проверяли».</summary>
    private static string ExpectedOwn(string pricingCheckedAt)
    {
        var own = PricingDecisions.CheckedDateText(pricingCheckedAt);

        return own.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.PeakCheckedFormat, own)
            : PanelStrings.PeakCheckedNever;
    }

    /// <summary>Профильная строка — ОТДЕЛЬНОЙ строкой и с подписью источника.</summary>
    private static string ExpectedProfile(AgentProfile agent) =>
        string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.PeakProfileCheckedFormat,
            PeakDecisions.CheckedText(agent));

    // ---------------------------------------------------------------- 1. короткая строка

    /// <summary>
    /// В ГЛАВНОМ ОКНЕ — ТОЛЬКО «ПРОВЕРЕНО КОГДА»: ни источника (URL), ни расписания, ни часов
    /// окон, ни часового пояса. Именно это и просил владелец; полное расписание осталось
    /// в настройках и в отдельном окне «Пики и тарифы».
    /// </summary>
    [AvaloniaFact]
    public void Строка_пиков_короткая_и_без_источника()
    {
        // Прогон без права читать ключ: запроса не будет вовсе; отметки разбора цен в настройках
        // по умолчанию НЕТ — панель ещё не разбирала страницу.
        var controller = Controller(new PanelSettings(), "нет-такого-файла.yaml", allowed: false);

        var window = new MainWindow();
        window.AttachBalance(controller);
        window.Show();
        PanelTestStand.Settle();

        var own = window.FindControl<TextBlock>("PeakScheduleText")?.Text ?? string.Empty;
        var profile = window.FindControl<TextBlock>("PeakProfileText")!;

        // СВОЯ строка: своей отметки нет — честное «не проверяли», а НЕ дата профиля
        // (подставить чужую дату значило бы соврать про свою проверку — F-5, 20.10.2026).
        Assert.Equal(PanelStrings.PeakCheckedNever, own);
        Assert.DoesNotContain(PeakDecisions.CheckedText(controller.Agent), own, StringComparison.Ordinal);

        // ПРОФИЛЬНАЯ — отдельной строкой и с подписью источника.
        Assert.Equal(ExpectedProfile(controller.Agent), profile.Text);
        Assert.True(profile.IsVisible, "дата из профиля движка не показана");
        Assert.Contains(
            PanelStrings.PeakProfileCheckedFormat.Split('{')[0].Trim(),
            profile.Text ?? string.Empty,
            StringComparison.Ordinal);

        // Обе строки короткие: ни источника (URL), ни расписания, ни часового пояса, ни ISO.
        foreach (var line in new[] { own, profile.Text ?? string.Empty })
        {
            Assert.DoesNotContain("http", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AgentCatalog.DeepSeek.PricingSource, line, StringComparison.Ordinal);
            Assert.DoesNotContain("01:00", line, StringComparison.Ordinal);
            Assert.DoesNotContain("06:00", line, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", line, StringComparison.Ordinal);
            Assert.DoesNotContain(AgentCatalog.DeepSeek.PricingChecked, line, StringComparison.Ordinal);
        }

        window.Close();
    }

    /// <summary>
    /// СВОЯ ДАТА И ДАТА ПРОФИЛЯ — ДВЕ РАЗНЫЕ СТРОКИ. Своя приходит из настроек панели
    /// (<see cref="PanelSettings.PricingCheckedAt"/>, формат <c>дд.ММ.гггг</c>), профильная — из
    /// профиля агента и ОТДЕЛЬНО, с подписью источника. Одна строка на обе давала бы человеку
    /// неверное чтение (замечание владельца 20.10.2026, F-5).
    ///
    /// ⚠️ Мутации, которые обязаны упасть ИМЕННО здесь: **«показывать только профильную»**
    /// (своя строка пошла бы из профиля), **«свою дату подставить чужим значением»** (в своей
    /// строке оказалась бы профильная дата) и **«подпись источника убрать»** (вторая строка
    /// осталась бы без подписи).
    /// </summary>
    [AvaloniaFact]
    public void Своя_дата_и_дата_профиля_двумя_разными_строками()
    {
        var settings = new PanelSettings { PricingCheckedAt = OwnCheckedStamp() };
        var controller = Controller(settings, "нет-такого-файла.yaml", allowed: false);

        var ownDate = OwnDate();
        var profileDate = PeakDecisions.CheckedText(AgentCatalog.DeepSeek);

        // Даты ДЕЙСТВИТЕЛЬНО разные — иначе проверка ниже ничего бы не различала.
        Assert.NotEqual(ownDate, profileDate);

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, PanelStrings.PeakCheckedFormat, ownDate),
            controller.PeakCheckedText);
        Assert.Equal(ExpectedProfile(AgentCatalog.DeepSeek), controller.PeakProfileCheckedText);

        Assert.Contains(ownDate, controller.PeakCheckedText, StringComparison.Ordinal);
        Assert.DoesNotContain(profileDate, controller.PeakCheckedText, StringComparison.Ordinal);
        Assert.Contains(profileDate, controller.PeakProfileCheckedText, StringComparison.Ordinal);
        Assert.NotEqual(controller.PeakCheckedText, controller.PeakProfileCheckedText);

        var window = new MainWindow();
        window.AttachBalance(controller);
        window.Show();
        PanelTestStand.Settle();

        var ownBlock = window.FindControl<TextBlock>("PeakScheduleText")!;
        var profileBlock = window.FindControl<TextBlock>("PeakProfileText")!;

        Assert.Equal(controller.PeakCheckedText, ownBlock.Text);
        Assert.Equal(controller.PeakProfileCheckedText, profileBlock.Text);
        Assert.True(profileBlock.IsVisible, "дата из профиля движка не показана");

        // ПОДПИСЬ ИСТОЧНИКА — у профильной строки и только у неё.
        var caption = PanelStrings.PeakProfileCheckedFormat.Split('{')[0].Trim();

        Assert.StartsWith(caption, profileBlock.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(caption, ownBlock.Text ?? string.Empty, StringComparison.Ordinal);

        window.Close();
    }

    // ---------------------------------------------------------------- 2. три состояния

    /// <summary>
    /// ФОРМА ОБЕИХ СТРОК ОДНА В ТРЁХ СОСТОЯНИЯХ БАЛАНСА. Даты проверки цен не зависят от того,
    /// спросили ли баланс: «ключ недоступен», «ответа нет» и «ответ есть» обязаны показывать одно
    /// и то же — и свою дату, и профильную. Иначе строки про пики мигали бы вместе с балансом,
    /// а это про разные вещи.
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
            var settings = new PanelSettings { PricingCheckedAt = OwnCheckedStamp() };

            // 1. Ключ недоступен: прогон без права читать ключ владельца.
            var locked = Controller(settings, Path.Combine(dir, "ключ.yaml"), allowed: false);
            Assert.False(locked.Allowed);

            // 2. Ответа нет: права есть, а ключа в файле нет.
            var noAnswer = Controller(settings, Path.Combine(dir, "пусто.yaml"), allowed: true);
            noAnswer.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !noAnswer.Busy, 5000), "запрос баланса не завершился");
            Assert.False(noAnswer.Result.Ok);

            // 3. Ответ есть: ключ на месте, клиент подставной.
            Directory.CreateDirectory(dir);
            var keyPath = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(keyPath, "DEEPSEEK_API_KEY: sk-1234567890abcdef\n", new UTF8Encoding(false));

            var answered = Controller(settings, keyPath, allowed: true);
            answered.Refresh();
            Assert.True(SpinWait.SpinUntil(() => !answered.Busy, 5000), "запрос баланса не завершился");
            Assert.True(answered.Result.Ok);

            // Три состояния ДЕЙСТВИТЕЛЬНО разные — иначе равенство ниже ничего не значит.
            Assert.NotEqual(locked.StatusText, answered.StatusText);

            var expectedOwn = ExpectedOwn(settings.PricingCheckedAt);
            var expectedProfile = ExpectedProfile(AgentCatalog.DeepSeek);

            Assert.Equal(expectedOwn, locked.PeakCheckedText);
            Assert.Equal(expectedOwn, noAnswer.PeakCheckedText);
            Assert.Equal(expectedOwn, answered.PeakCheckedText);

            Assert.Equal(expectedProfile, locked.PeakProfileCheckedText);
            Assert.Equal(expectedProfile, noAnswer.PeakProfileCheckedText);
            Assert.Equal(expectedProfile, answered.PeakProfileCheckedText);
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

        // СВОЯ отметка — только дата, и она МЕСТНАЯ: полдень местного дня, чтобы дата не зависела
        // от часового пояса машины (полночь соседнего пояса дала бы вчерашний день).
        Assert.Equal(OwnDate(), PricingDecisions.CheckedDateText(PricingDecisions.Stamp(new DateTimeOffset(DateTime.Today.AddHours(12)))));

        // Разобрать нельзя — «отметки нет»: своя строка скажет «не проверяли», а не выдумает дату.
        foreach (var garbage in new[] { string.Empty, "не дата", "2026-13-45" })
        {
            Assert.Equal(string.Empty, PricingDecisions.CheckedDateText(garbage));
        }
    }

    /// <summary>
    /// ТРИ ЯЗЫКА ПОКРЫТЫ ОДИНАКОВО. Своя строка, честная «не проверяли» и подпись источника
    /// у профильной даты есть во всех трёх словарях, и в каждом языке из них собираются ДВЕ РАЗНЫЕ
    /// строки: своя дата и дата профиля с подписью. Смысл перевода проверяет человек — здесь
    /// механически проверяются наличие, подстановка и различие двух строк.
    /// </summary>
    [Fact]
    public void Две_даты_названы_на_всех_трёх_языках()
    {
        foreach (var language in Loc.Known)
        {
            var ownFormat = Loc.TIn(language, nameof(PanelStrings.PeakCheckedFormat));
            var never = Loc.TIn(language, nameof(PanelStrings.PeakCheckedNever));
            var profileFormat = Loc.TIn(language, nameof(PanelStrings.PeakProfileCheckedFormat));

            Assert.False(string.IsNullOrWhiteSpace(ownFormat), $"{language}: нет строки «проверено»");
            Assert.False(string.IsNullOrWhiteSpace(never), $"{language}: нет строки «не проверяли»");
            Assert.False(string.IsNullOrWhiteSpace(profileFormat), $"{language}: нет подписи источника");

            var caption = profileFormat.Split('{')[0].Trim();

            Assert.True(caption.Length > 0, $"{language}: у профильной строки НЕТ подписи источника");
            Assert.NotEqual(ownFormat, profileFormat);

            var own = string.Format(CultureInfo.CurrentCulture, ownFormat, "19.10.2026");
            var profile = string.Format(CultureInfo.CurrentCulture, profileFormat, "24.09.2026");

            Assert.Contains("19.10.2026", own, StringComparison.Ordinal);
            Assert.Contains("24.09.2026", profile, StringComparison.Ordinal);
            Assert.DoesNotContain("24.09.2026", own, StringComparison.Ordinal);
            Assert.NotEqual(own, profile);
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
