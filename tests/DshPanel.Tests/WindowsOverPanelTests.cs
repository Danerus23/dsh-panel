using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using DshPanel.Server;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОКНА ПАНЕЛИ СТОЯТ ПОВЕРХ НЕЁ, А ПАНЕЛЬ ПРИ ЭТОМ НЕ ЗАКРЫВАЕТСЯ — И ДОКУМЕНТЫ ГОВОРЯТ ТО ЖЕ (DEF-039, 2.4.1).
///
/// **От какого случая.** Решение владельца 29.09.2026: *«хочу чтобы окна открывались поверх основной
/// панели, сейчас они открываются или слева или справа далеко»* — код с тех пор ставит вспомогательное
/// окно ПОВЕРХ панели по её центру и панель никогда не прячет. А три текста говорили другое:
/// комментарий `WindowPlacement.NearPanel` обещал, что панель при этом закроется (код её не прятал
/// ни дня), `STACK.md` §35 обещал окно рядом, а не поверх (отменённое решение 28.09.2026),
/// а в том же файле осталась мёртвая ветка `Beside` — второй ответ на тот же вопрос. Дефект слоя
/// «документы против кода»: человек и агент, читая текст, получали неверную картину поведения.
///
/// **Что доказывается.** Два разных утверждения, и оба обязательны:
/// 1. **живая проба** — два НАСТОЯЩИХ окна (панель и настройки) с настоящими размерами, место ставит
///    та же дверь `PanelShell.Place`, что и в продукте: окно накрывает панель по ВСЕМ четырём краям,
///    остаётся в пределах рабочего стола, а панель после этого ВИДИМА (её никто не закрыл и не спрятал);
/// 2. **сверка строк** — в комментарии и в документах нет прежних ложных формулировок и есть
///    действующая, а мёртвой ветки «рядом» в ядре больше нет.
///
/// ⚠️ **ЦЕНТР ОКНА ПО ЦЕНТРУ ПАНЕЛИ — ПРАВИЛО ЯДРА, А НЕ ЭТОЙ ПРОБЫ, и это названо, а не спрятано.**
/// Окно настроек (1080×900) БОЛЬШЕ панели (640×640): `OverPanel` центрирует его по панели, а
/// `Visible` прижимает внутрь рабочего стола — и после прижатия центр окна с центром панели не
/// совпадает. Равенство центров доказано там, где оно возможно, подставными прямоугольниками
/// (`WindowPlacementTests.Панель_на_экране_окно_встаёт_поверх_неё_по_центру`: окно 640×640
/// в панели 860×720). Ни одно окно-спутник сегодня в панель целиком не помещается (каждое шире
/// или выше её), поэтому живого случая «центр совпал» у этой пробы нет; появится такой спутник —
/// проверка ниже (`FitsInsidePanel`) потребует равенства центров здесь же.
///
/// **Мутации, которые обязаны упасть:** вернуть постановку окна РЯДОМ (вместо `OverPanel`) → красная
/// первая; вернуть в комментарий `WindowPlacement` слово о закрытии панели → красная вторая;
/// вернуть в `STACK.md` §35 обещание окна рядом → красная вторая; вернуть метод `Beside` → красная
/// вторая.
/// </summary>
public class WindowsOverPanelTests
{
    /// <summary>Рабочий стол, на котором стоят оба окна: он больше и панели, и настроек.</summary>
    private static readonly PixelRect[] Desktop = { new(0, 0, 1920, 1080) };

    [AvaloniaFact]
    public void Окно_настроек_накрывает_панель_а_панель_остаётся_видимой()
    {
        var dir = TempRoots.For("windows-over-panel");
        var panel = PanelTestStand.MainStand(ServerState.Stopped(0));
        var settings = PanelTestStand.SettingsStand(dir);

        try
        {
            panel.Show();
            settings.Show();
            PanelTestStand.Settle();

            // Размеры берутся У САМИХ ОКОН, а не литералами: разметка панели и настроек меняется,
            // и проверка обязана оставаться про поведение, а не про сегодняшние числа.
            var panelFrame = new PixelRect(
                panel.Position.X,
                panel.Position.Y,
                (int)Math.Round(panel.Width),
                (int)Math.Round(panel.Height));

            Assert.True(panelFrame.Width > 0 && panelFrame.Height > 0, "у окна панели нет размера — мерить нечего");
            Assert.True(settings.Width > panelFrame.Width / 2.0, "окно настроек не шире половины панели — опыт не про «поверх»");

            // ТА ЖЕ ДВЕРЬ, ЧТО В ПРОДУКТЕ (`PanelShell.Place`), — не своя арифметика в проверке.
            PanelShell.Place(settings, panelFrame, cursor: null, Desktop, scale: 1);
            PanelTestStand.Settle();

            var frame = new PixelRect(
                settings.Position.X,
                settings.Position.Y,
                (int)Math.Round(settings.Width),
                (int)Math.Round(settings.Height));

            // (1) ЦЕНТР — ТОЛЬКО ТАМ, ГДЕ ОН ВОЗМОЖЕН. Окно БОЛЬШЕ панели: `OverPanel` центрирует
            // его по панели, а `Visible` прижимает внутрь рабочего стола — и равенства центров
            // после прижатия не бывает. Обещано другое, и оно проверяется ниже: окно накрывает
            // панель целиком, а панель остаётся видимой. Само правило центра («поверх, по центру»)
            // доказано подставными прямоугольниками там, где окно в панель помещается
            // (`WindowPlacementTests.Панель_на_экране_окно_встаёт_поверх_неё_по_центру`).
            var fitsInsidePanel = frame.Width <= panelFrame.Width && frame.Height <= panelFrame.Height;

            if (fitsInsidePanel)
            {
                Assert.Equal(panelFrame.X + (panelFrame.Width / 2), frame.X + (frame.Width / 2));
                Assert.Equal(panelFrame.Y + (panelFrame.Height / 2), frame.Y + (frame.Height / 2));
            }

            // (2) И НАКРЫВАЕТ ЕЁ ЦЕЛИКОМ ПО ОБЕИМ ОСЯМ: «поверх панели» — это не «пересекается углом».
            Assert.True(frame.X <= panelFrame.X, "окно не накрывает левый край панели");
            Assert.True(frame.X + frame.Width >= panelFrame.X + panelFrame.Width, "окно не накрывает правый край панели");
            Assert.True(frame.Y <= panelFrame.Y, "окно не накрывает верхний край панели");
            Assert.True(frame.Y + frame.Height >= panelFrame.Y + panelFrame.Height, "окно не накрывает нижний край панели");

            // (3) И ОКНО ОСТАЛОСЬ НА РАБОЧЕМ СТОЛЕ: прижатие внутрь экрана — часть того же обещания,
            // иначе кнопки закрытия у окна не найти («окно у края» — класс дефекта DEF-039).
            var desktop = Desktop[0];

            Assert.True(
                frame.X >= desktop.X && frame.Y >= desktop.Y
                && frame.X + frame.Width <= desktop.X + desktop.Width
                && frame.Y + frame.Height <= desktop.Y + desktop.Height,
                $"окно настроек вылезло за рабочий стол: {frame} при столе {desktop}");

            // (4) ПАНЕЛЬ НЕ ЗАКРЫТА И НЕ СПРЯТАНА: оба окна видимы. Это и есть та часть обещания,
            // которую комментарий называл наоборот.
            Assert.True(panel.IsVisible, "панель перестала быть видимой, хотя по решению она остаётся за окном");
            Assert.True(settings.IsVisible, "окна настроек не видно — мерить «поверх» нечего");
        }
        finally
        {
            settings.Close();
            panel.Close();
            TempRoots.Remove(dir);
        }
    }

    [Fact]
    public void Комментарий_и_документы_говорят_то_же_что_код()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        var placement = Read(root, "src", "DshPanel", "Shell", "WindowPlacement.cs");
        var shell = Read(root, "src", "DshPanel", "Shell", "PanelShell.cs");
        var window = Read(root, "src", "DshPanel", "Shell", "PanelWindow.cs");
        // ВНУТРЕННИЕ ДОКУМЕНТЫ ЕСТЬ НЕ ВСЕГДА: публичное дерево выпуска их не содержит (`docs/**` снимается
        // фильтром выпуска). Поэтому §35 проверяется там, где документ есть, — а слова о поведении в коде
        // и в комментариях проверок проверяются ВСЕГДА. Иначе рабочая проверка зеленела бы на приватном
        // дереве и краснела на публичном (так и случилось на первом прогоне конвейера 2.4.1).
        var stackPath = Path.Combine(root, "docs", "STACK.md");
        var stack = File.Exists(stackPath) ? File.ReadAllText(stackPath) : string.Empty;

        // ЛОЖНЫЕ ФОРМУЛИРОВКИ ПРЕЖНЕГО РЕШЕНИЯ. Каждая жила в дереве 2.4.0 и врала о поведении.
        Assert.DoesNotContain("панель при этом закрывается", placement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ПОСТАВИТЬ ВСПОМОГАТЕЛЬНОЕ ОКНО РЯДОМ С ПАНЕЛЬЮ", shell, StringComparison.Ordinal);

        if (stack.Length > 0) Assert.DoesNotContain("рядом с ней и НЕ поверх", stack, StringComparison.Ordinal);

        // ДЕЙСТВУЮЩАЯ ФОРМУЛИРОВКА — в комментарии и в §35, и она одна на все три файла.
        Assert.Contains("ПОВЕРХ панели", placement, StringComparison.Ordinal);
        Assert.Contains("ПОВЕРХ ПАНЕЛИ", shell, StringComparison.Ordinal);

        if (stack.Length > 0)
        {
            var section = Section(stack, "35. **МЕСТО ВСПОМОГАТЕЛЬНОГО ОКНА");
            Assert.False(string.IsNullOrEmpty(section), "§35 о месте вспомогательного окна не найден — сторож ослеп");
            Assert.Contains("ПОВЕРХ неё, по её центру", section!, StringComparison.Ordinal);
            Assert.Contains("не закрывается", section!, StringComparison.Ordinal);
        }

        // И ПАНЕЛЬ ДЕЙСТВИТЕЛЬНО НЕ ПРЯЧЕТСЯ: в двери размещения нет ни одного `Hide`.
        //
        // ⚠️ Второй якорь — ПО-РУССКИ («ПАНЕЛЬ УШЛА В ТРЕЙ»): в первой редакции здесь стояло
        // латинское «PANEL», проверка не находила конца двери и падала сторожем, ни разу не
        // заглянув в её тело. Теперь сторож смотрит ровно туда, куда обещал; падение доказано
        // мутацией «позвать Hide() в `PanelShell.Place`».
        var place = Between(shell, "private void Place(Window window)", "ПАНЕЛЬ УШЛА В ТРЕЙ");
        Assert.False(string.IsNullOrEmpty(place), "не видно тела двери `PanelShell.Place` — сторож ослеп");
        Assert.DoesNotContain("Hide(", place!, StringComparison.Ordinal);
        Assert.DoesNotContain("Close(", place!, StringComparison.Ordinal);

        // ВТОРОГО ОТВЕТА В ЯДРЕ НЕТ: мёртвая ветка «окно рядом» удалена, а не оставлена «на всякий».
        Assert.DoesNotContain("private static PixelRect Beside(", placement, StringComparison.Ordinal);
        Assert.DoesNotContain("PanelGap", placement, StringComparison.Ordinal);

        // И слово «поверх» стоит в том же файле, который считает место.
        Assert.Contains("ПОВЕРХ неё", placement, StringComparison.Ordinal);
        Assert.Contains("по центру", placement, StringComparison.Ordinal);
        Assert.Contains("ПОВЕРХ неё", window, StringComparison.Ordinal);

        // ВТОРОЕ ЛЕЧЕНИЕ КОРНЯ (repair t18, находка V1). Сторож читал только три файла кода и §35 —
        // поэтому ложное «рядом» и выжило: оно жило в КОММЕНТАРИЯХ ПРОВЕРОК, куда он не смотрел.
        // Теперь те же слова сверяются и в них: отменённую формулировку нельзя вернуть ни сюда,
        // ни в код, не покраснев.
        var shellTests = Read(root, "tests", "DshPanel.Tests", "PanelShellTests.cs");
        var placementTests = Read(root, "tests", "DshPanel.Tests", "WindowPlacementTests.cs");

        Assert.DoesNotContain("окно рядом", shellTests, StringComparison.Ordinal);
        Assert.Contains("окно ПОВЕРХ неё, по её центру", shellTests, StringComparison.Ordinal);

        Assert.DoesNotContain("вспомогательного окна: рядом с панелью", placementTests, StringComparison.Ordinal);
        Assert.DoesNotContain("мониторе, рядом с панелью", placementTests, StringComparison.Ordinal);

        // СЛЕД УДАЛЁННОЙ ВЕТКИ `Beside` (repair t20, находка V2-F1): «места «рядом» на узком экране
        // нет вовсе» — это про путь, которого в коде больше нет; действующее правило — «поверх»,
        // а узкий экран только прижимает окно внутрь. Сверка без учёта регистра: след возвращается
        // и с заглавной буквы.
        Assert.DoesNotContain("места «рядом»", placementTests, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("поверх панели, по её центру", placementTests, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(root, Path.Combine(parts)));

    /// <summary>Кусок файла от первого вхождения заголовка до следующего раздела жирным текстом.</summary>
    private static string Section(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var end = text.IndexOf("\n36.", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : text[start..end];
    }
}
