using System;
using System.Collections.Generic;
using System.Threading;
using DshPanel.Pricing;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Update;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СТРОКА ОБНОВЛЕНИЯ В МЕНЮ ЗНАЧКА — четвёртая строка состояния, рядом с сервером, агентом
/// и тарифом.
///
/// Проверка перебирает СОСТОЯНИЯ (как <c>TrayStatusTests</c> у трёх соседних строк): «проверено
/// 28.09.2026, 14:32», «доступна версия 2.1.0», «эта версия пропущена», «ещё не проверяли»
/// и «проверить не удалось» — пять разных ответов, и подменять один другим нельзя. Строка в меню
/// читается человеком, который окно не открывал: неправда здесь дороже, чем в окне.
///
/// ⚠️ Отдельно сторожится ПРОПУСК: ответ человека глушит шарик про ЭТУ версию и НЕ глушит его
/// про следующую (иначе один пропуск выключил бы извещения навсегда). Это правило ядра
/// (<see cref="UpdateDecisions.ShouldAnnounce"/>), и оно проверяется здесь вместе с шариком.
/// </summary>
public class UpdateTrayLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Подстановка вместо сети: отдаёт заранее записанный ответ и считает вызовы.</summary>
    private sealed class FakeClient : IUpdateClient
    {
        private readonly Func<UpdateRelease> _answer;

        public FakeClient(Func<UpdateRelease> answer) => _answer = answer;

        public int Calls { get; private set; }

        public UpdateRelease Latest(string language, DateTimeOffset now)
        {
            Calls++;

            return _answer();
        }
    }

    private static UpdateRelease Release(string version = "v2.1.0") => new(
        true,
        string.Empty,
        version,
        ProductLinks.ReleasesPageUrl,
        "2026-09-27T10:00:00Z",
        "Русские заметки",
        "## RU\n\nРусские заметки",
        Now);

    private static UpdateController Controller(
        IUpdateClient client,
        PanelSettings settings,
        bool allowed = true,
        string current = "2.0.0") =>
        new(
            client,
            () => settings,
            () => "ru",
            () => current,
            allowed,
            () => Now,
            action => action(),
            _ => { },
            _ => { },
            _ => { });

    private static void Wait(UpdateController controller)
    {
        Assert.True(
            SpinWait.SpinUntil(() => !controller.Busy, 5000),
            "проверка не завершилась");
    }

    /// <summary>
    /// СОСТОЯНИЕ ПОСЛЕ ПРОВЕРКИ: выпуск новее — «доступна версия», и тон «внимание»: это не беда,
    /// но и не «всё в порядке».
    /// </summary>
    [Fact]
    public void Строка_называет_новую_версию_и_просит_внимания()
    {
        var settings = new PanelSettings();
        var controller = Controller(new FakeClient(() => Release()), settings);

        controller.Check();
        Wait(controller);

        Assert.True(controller.Newer);

        var line = controller.TrayLine;

        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateAvailableFormat, "v2.1.0"),
            line.Text);
        Assert.Equal(TrayTone.Warning, line.Tone);
    }

    /// <summary>
    /// «ПРОВЕРЕНО ДД.ММ.ГГГГ» — только когда новее ничего нет. Дата берётся у отметки проверки,
    /// а не выдумывается: именно её человек и ждёт от строки в меню.
    /// </summary>
    [Fact]
    public void Строка_называет_дату_проверки_когда_новее_ничего_нет()
    {
        var settings = new PanelSettings();
        var controller = Controller(new FakeClient(() => Release("v2.0.0")), settings);

        controller.Check();
        Wait(controller);

        Assert.False(controller.Newer);

        var line = controller.TrayLine;

        // Дата берётся у ТОЙ ЖЕ отметки, что записана в настройки, а не «похожая»: строка
        // обязана называть момент проверки.
        Assert.Contains(
            PricingDecisions.CheckedText(PricingDecisions.Stamp(Now)),
            line.Text,
            StringComparison.Ordinal);
        Assert.Equal(TrayTone.Good, line.Tone);
    }

    /// <summary>
    /// ПРОПУЩЕННАЯ ВЕРСИЯ: строка говорит о решении ЧЕЛОВЕКА (и он его принял), а не о новизне.
    /// Тон — серый: это не «внимание», напоминать не о чем.
    /// </summary>
    [Fact]
    public void Пропущенная_версия_названа_и_не_красится_в_внимание()
    {
        var settings = new PanelSettings { UpdateSkippedVersion = "v2.1.0" };
        var controller = Controller(new FakeClient(() => Release()), settings);

        controller.Check();
        Wait(controller);

        Assert.True(controller.Skipped);

        var line = controller.TrayLine;

        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.UpdateSkippedFormat, "v2.1.0"),
            line.Text);
        Assert.Equal(TrayTone.Neutral, line.Tone);
    }

    /// <summary>
    /// ДАННЫХ НЕТ — строка не выдумывает ни версию, ни дату: она называет причину (не проверяли,
    /// не вышло, прогон без права) и остаётся серой.
    /// </summary>
    [Fact]
    public void Без_данных_строка_называет_причину_а_не_дату()
    {
        var never = Controller(new FakeClient(() => Release()), new PanelSettings()).TrayLine;

        Assert.Equal(PanelStrings.UpdateNeverChecked, never.Text);
        Assert.Equal(TrayTone.Neutral, never.Tone);

        var locked = Controller(new FakeClient(() => Release()), new PanelSettings(), allowed: false).TrayLine;

        Assert.Equal(PanelStrings.UpdateLocked, locked.Text);
        Assert.Equal(TrayTone.Neutral, locked.Tone);
    }

    /// <summary>Отказ сети назван словами и в строке меню: молчания там быть не может.</summary>
    [Fact]
    public void Отказ_сети_виден_в_строке_меню()
    {
        var controller = Controller(
            new FakeClient(() => UpdateRelease.Failed(PanelStrings.UpdateRateLimited, Now)),
            new PanelSettings());

        controller.Check();
        Wait(controller);

        var line = controller.TrayLine;

        Assert.Contains(PanelStrings.UpdateRateLimited, line.Text, StringComparison.Ordinal);
        Assert.Equal(TrayTone.Warning, line.Tone);
    }

    /// <summary>
    /// ПРОПУСК ГЛУШИТ ШАРИК ПРО ЭТУ ВЕРСИЮ И НЕ ГЛУШИТ ПРО СЛЕДУЮЩУЮ.
    ///
    /// Первое — ответ человека и его исполнение. Второе — иначе один пропуск выключил бы
    /// извещения навсегда, и человек не узнал бы о выпуске, который сам же и ждал.
    ///
    /// ⚠️ Шарик даёт РАСПИСАНИЕ (<c>Tick</c>), а не щелчок человека: «Проверить сейчас» шарика
    /// не даёт вовсе — человек сам попросил и сам увидел ответ. Поэтому проверка идёт тактом.
    /// </summary>
    [Fact]
    public void Пропуск_глушит_шарик_про_эту_версию_и_не_глушит_про_следующую()
    {
        var skipped = new PanelSettings { UpdateSkippedVersion = "2.1.0" };
        var told = new List<string>();

        var same = Controller(new FakeClient(() => Release("v2.1.0")), skipped);
        same.Announce += told.Add;
        same.Tick(Now);
        Wait(same);

        Assert.True(same.Result.Ok);
        Assert.True(same.Skipped);
        Assert.Empty(told);

        var next = Controller(new FakeClient(() => Release("v2.3.0")), skipped);
        next.Announce += told.Add;
        next.Tick(Now);
        Wait(next);

        Assert.False(next.Skipped);
        Assert.Equal(new[] { "v2.3.0" }, told);
    }

    /// <summary>
    /// ПЕРЕЗАПУСК ПАНЕЛИ В ТЕ ЖЕ СУТКИ. Суточный гейт закрыт отметкой, проверки не будет — значит
    /// состояние ОБЯЗАНО прийти из настроек: иначе человек увидел бы «ещё не проверяли» через час
    /// после проверки, а панель «забыла» бы о новом выпуске до завтра.
    ///
    /// ⚠️ И заметки при этом пересобираются НА ЯЗЫКЕ ПАНЕЛИ из сырого тела выпуска: язык мог
    /// смениться, и заметки обязаны следовать за ним, а не ждать следующей проверки.
    /// </summary>
    [Fact]
    public void Состояние_переживает_перезапуск_панели_и_заметки_идут_за_языком()
    {
        var settings = new PanelSettings
        {
            UpdateLatest = "v2.1.0",
            UpdateCheckedAt = PricingDecisions.Stamp(Now.AddHours(-1)),
            UpdateNotes = "Русские заметки",
            UpdateNotesRaw = "## RU\n\nРусские заметки\n\n## ZH\n\n中文说明",
            UpdatePageUrl = ProductLinks.ReleasesPageUrl,
        };

        var client = new FakeClient(() => Release());

        var restored = new UpdateController(
            client,
            () => settings,
            () => "zh",
            () => "2.0.0",
            allowed: true,
            clock: () => Now,
            dispatch: action => action(),
            log: _ => { },
            remember: _ => { },
            rememberCheckedAt: _ => { });

        // Проверки не было вовсе: суточный гейт закрыт отметкой час назад.
        restored.Tick(Now);

        Assert.Equal(0, client.Calls);
        Assert.True(restored.Result.Ok);
        Assert.True(restored.Newer);
        Assert.Equal("中文说明", restored.Notes);

        // И версия в строке меню — та самая, а не пустота.
        Assert.Contains("v2.1.0", restored.TrayLine.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПРОГОН БЕЗ ПРАВА не только не ходит в сеть — он и состояние из настроек не подставляет:
    /// в прогоне проверки панель не знает о выпусках НИЧЕГО, и строка говорит об этом прямо.
    /// </summary>
    [Fact]
    public void В_прогоне_без_права_состояние_из_настроек_не_подставляется()
    {
        var settings = new PanelSettings
        {
            UpdateLatest = "v2.1.0",
            UpdateCheckedAt = PricingDecisions.Stamp(Now.AddHours(-1)),
        };

        var controller = Controller(new FakeClient(() => Release()), settings, allowed: false);

        Assert.False(controller.Result.Ok);
        Assert.Equal(PanelStrings.UpdateLocked, controller.TrayLine.Text);
    }

    /// <summary>
    /// ПУСТАЯ ОТМЕТКА ИЛИ МУСОР — «не проверяли»: показать версию без даты значило бы выдумать,
    /// что её вообще видели.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("вчера")]
    public void Версия_без_разобранной_отметки_не_показывается(string stamp)
    {
        var settings = new PanelSettings { UpdateLatest = "v2.1.0", UpdateCheckedAt = stamp };

        var controller = Controller(new FakeClient(() => Release()), settings);

        Assert.False(controller.Result.Ok);
        Assert.Equal(PanelStrings.UpdateNeverChecked, controller.TrayLine.Text);
    }
}
