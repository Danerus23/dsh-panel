using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// КЛЮЧИ КАДРА — ТЕМА И СОСТОЯНИЕ — ДОЕЗЖАЮТ ДО НАРИСОВАННОГО ОКНА, А НЕ ТОЛЬКО ДО РАЗБОРА.
///
/// **От каких случаев (брифинг фазы 3, список обязанных падать проверок).** В нём названы прямо:
/// «**тёмная тема не применилась**» и «**фокус не виден**». До этого класса обе держались на глазах:
/// разбор ключей проверялся (`ShotDecisionsTests`), а вот **применение** — нет вовсе. Мутация
/// **AJ3** («тёмная тема не применяется») именно поэтому не имела якоря и названа в `HISTORY.md`
/// как непокрытое.
///
/// **Как меряется.** Окно строится **тем же членом, что съёмка** (`Program.ShotWindowWithDoors`),
/// показывается, тема ставится **тем же членом, что съёмка** (`App.ApplyTheme`), а состояние —
/// **тем же** (`Program.ShotState`). Дальше читаются ПИКСЕЛИ нарисованного окна: «темнее» — это
/// число, а не свойство, и «фокус видно» — тоже число, а не обещание.
///
/// ⚠️ **Чего класс НЕ проверяет.** Как это выглядит глазом: темная тема может быть тёмной
/// и при этом некрасивой, а обводка фокуса — видимой и при этом уродливой. Это читает человек
/// по кадру.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class ShotStateTests
{
    /// <summary>Сервер окна — как у съёмки: прогон проверки порт владельца не занимает.</summary>
    private static ServerController ShotServer(AppPaths paths) =>
        new(
            paths.DshHome,
            () => paths.DataDir,
            _ => { },
            mayOccupyOwnerPort: false,
            probe: ServerProbe.System(),
            ownEngineDir: () => paths.EngineDir);

    /// <summary>
    /// ТЁМНАЯ ТЕМА КАДРА НАРИСОВАНА ТЕМНЕЕ СВЕТЛОЙ — и это ОДНО И ТО ЖЕ окно.
    ///
    /// **От какого случая.** Ключ `--theme dark` меняет `RequestedThemeVariant` приложения; если
    /// окно построено РАНЬШЕ этого (или тема не применена вовсе), кадр выйдет светлым, а имя ключа
    /// останется обещанием. Именно эту подмену делает мутация **AJ3**.
    ///
    /// ⚠️ Мерится **средняя яркость** нарисованного окна, а не имя варианта: имя можно выставить
    /// и не увидеть ничего. Порог намеренно грубый (темнее хотя бы на пятую часть) — он ловит
    /// «тема не применилась» и «применилась к другому окну», но не придирается к оттенку.
    /// </summary>
    [AvaloniaFact]
    public void Тёмная_тема_кадров_рисуется_темнее_светлой()
    {
        // ⚠️ И ПЕРЕВОД «значение ключа → тема панели» — тоже проверяемое решение: ошибись он
        // (тёмная ставит светлую), и съёмка молча сняла бы не тот кадр.
        Assert.Equal(PanelTheme.Light, ShotDecisions.Palette(ShotDecisions.ThemeLight));
        Assert.Equal(PanelTheme.Dark, ShotDecisions.Palette(ShotDecisions.ThemeDark));
        Assert.Null(ShotDecisions.Palette(null));
        Assert.Null(ShotDecisions.Palette("night"));

        var tested = 0;

        foreach (var kind in new[] { string.Empty, StartModes.ShotSettingsGeneral, StartModes.ShotPeaks })
        {
            var light = MeanBrightness(kind, ShotDecisions.ThemeLight);
            var dark = MeanBrightness(kind, ShotDecisions.ThemeDark);

            var name = kind.Length == 0 ? "(без имени)" : kind;

            // ⚠️ Окно обязано быть НАРИСОВАННЫМ: у невидимого кадра яркость 0 в обеих темах,
            // и «темнее» совпало бы с «ничего не нарисовано».
            Assert.True(light > 0, $"кадр «{name}»: светлый кадр не нарисован (яркость {light})");

            Assert.True(
                dark < light * 0.8,
                $"кадр «{name}»: тёмная тема почти не отличается от светлой "
                + $"(яркость {dark.ToString("0.#", CultureInfo.InvariantCulture)} против "
                + $"{light.ToString("0.#", CultureInfo.InvariantCulture)}) — ключ `--theme dark` не доехал до окна");

            tested++;
        }

        Assert.Equal(3, tested);
    }

    /// <summary>
    /// ФОКУС И НАВЕДЕНИЕ ВИДНЫ НА НАРИСОВАННОМ ОКНЕ — и видны ИМЕННО ЧЕРЕЗ ТОТ ЖЕ ЧЛЕН, ЧТО СЪЁМКА.
    ///
    /// **От какого случая.** Ключ `--state focus:ИмяОргана` ставит псевдоклассы `:focus`
    /// и `:focus-visible` на живое дерево; кадр уезжает в приёмку как «вот фокус». Подмени
    /// `ShotState` — и кадр состояния выйдет БЕЗ состояния, а имя ключа останется обещанием.
    ///
    /// ⚠️ **Сравниваются ДВА отпечатка одного и того же окна:** до состояния и после. Так проверка
    /// не зависит от того, как именно выглядит обводка (её может переопределить тема каркаса),
    /// и падает ровно на «состояние не нарисовалось».
    ///
    /// ⚠️ **Чего здесь нет:** настоящих нажатий Tab. `:focus-visible` браузер (и Avalonia) зажигает
    /// по СПОСОБУ получения фокуса, поэтому обход Tab'ом проверяется там, где нажатие настоящее —
    /// в браузерном прогоне телефонной страницы (`PhoneBrowserTests.Обход_tab_ом_даёт_видимый_фокус…`).
    /// </summary>
    [AvaloniaFact]
    public void Состояние_органа_видно_на_кадре()
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);
        var window = Program.ShotWindowWithDoors(StartModes.ShotSettingsGeneral, paths, ShotServer(paths)).Window;

        window.Show();
        PanelTestStand.Settle();

        try
        {
            var save = window.FindControl<Button>("SaveButton");

            Assert.NotNull(save);

            // ⚠️ СРАВНИВАЕТСЯ ОБЛАСТЬ САМОГО ОРГАНА, а не всё окно: у окна настроек есть часы
            // и живые строки, и отпечаток ВСЕГО окна менялся бы сам по себе — «состояние видно»
            // зеленело бы на кадре, где состояния нет (проверено прогоном: первая редакция этой
            // проверки пережила мутацию «ветвь фокуса отключена» именно так).
            var box = Frame(window, save);
            var quiet = PanelTestStand.ColorsIn(window, box);

            // ФОКУС — тот же член, каким его ставит съёмка.
            Assert.True(
                Program.ShotState(window, ShotDecisions.StateFocus, "SaveButton"),
                "состояние фокуса не поставилось на орган, который в окне есть");

            PanelTestStand.Settle();

            // Обводка фокуса — это ДОБАВЛЕННЫЕ цвета: на месте органа их становится больше.
            var focused = PanelTestStand.ColorsIn(window, box);

            Assert.True(
                focused > quiet,
                $"фокуса на органе не видно: цветов в нём было {quiet}, стало {focused}");

            // И ЭТО ИМЕННО ФОКУС, А НЕ ЧТО УГОДНО: псевдоклассы проверяются у того же органа.
            Assert.Contains(":focus-visible", save.Classes);
            Assert.Contains(":focus", save.Classes);

            // НАВЕДЕНИЕ — отдельным состоянием: чек-лист вида требует оба.
            Assert.True(
                Program.ShotState(window, ShotDecisions.StateHover, "SaveButton"),
                "состояние наведения не поставилось на орган, который в окне есть");

            PanelTestStand.Settle();

            Assert.Contains(":pointerover", save.Classes);

            // ⚠️ И НАВЕДЕНИЕ ВИДНО НАРИСОВАННЫМ, а не только «псевдокласс стоит»: прежде
            // проверка мерила НАРИСОВАННОЕ только у фокуса, а у наведения довольствовалась
            // псевдоклассом — то есть мутация «ветвь наведения ничего не делает» выжила бы.
            //
            // ⚠️ Меряется НА СВЕЖЕМ ОКНЕ, БЕЗ ФОКУСА: на органе с уже поставленным `:focus`
            // наведение не добавляет ни одного цвета (замер прогоном), и проверка краснела бы
            // на «состояния накладываются», а не на «наведение не рисуется».
            var fresh = Program.ShotWindowWithDoors(StartModes.ShotSettingsGeneral, paths, ShotServer(paths)).Window;

            fresh.Show();
            PanelTestStand.Settle();

            try
            {
                // ⚠️ Мерится ОТПЕЧАТОК ОКНА, а не число цветов в области органа: замер прогоном
                // показал, что наведение МЕНЯЕТ ОТТЕНКИ, не добавляя новых цветов (177 и 177),
                // и «цветов столько же» краснело бы на исправном наведении.
                var plain = PanelTestStand.FrameHash(fresh);

                Assert.True(
                    Program.ShotState(fresh, ShotDecisions.StateHover, "SaveButton"),
                    "наведение не поставилось на свежем окне");

                PanelTestStand.Settle();

                Assert.NotEqual(plain, PanelTestStand.FrameHash(fresh));
            }
            finally
            {
                fresh.Close();
            }

            // И ПЕРЕВОД СОСТОЯНИЯ В ПСЕВДОКЛАССЫ — ОТДЕЛЬНЫМ РЕШЕНИЕМ, а не ветвью в съёмке:
            // фокус — ДВА псевдокласса, наведение — один, незнакомое — ни одного.
            Assert.Equal(new[] { ":focus", ":focus-visible" }, ShotDecisions.PseudoClasses(ShotDecisions.StateFocus));
            Assert.Equal(new[] { ":pointerover" }, ShotDecisions.PseudoClasses(ShotDecisions.StateHover));
            Assert.Empty(ShotDecisions.PseudoClasses("pressed"));
            Assert.Empty(ShotDecisions.PseudoClasses(null));

            // И ОТКАЗ НАЗВАН: незнакомого органа в окне нет, и это не молчание, а `false`.
            Assert.False(
                Program.ShotState(window, ShotDecisions.StateFocus, "Givotnoe"),
                "состояние поставилось на орган, которого в окне нет");
        }
        finally
        {
            window.Close();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// Прямоугольник органа в точках ОКНА — по нему меряется «видно ли состояние».
    ///
    /// Считается переводом точки через самого органа (`TranslatePoint` до окна), а не по
    /// `Bounds`: у органа внутри прокрутки и отступов одно с другим не совпадает, и мерить
    /// пришлось бы не то место.
    /// </summary>
    private static Rect Frame(Window window, Control control)
    {
        var at = control.TranslatePoint(default, window) ?? default;

        return new Rect(at, control.Bounds.Size);
    }

    /// <summary>
    /// СРЕДНЯЯ ЯРКОСТЬ НАРИСОВАННОГО ОКНА КАДРА В НАЗВАННОЙ ТЕМЕ — одним числом.
    ///
    /// Окно строится и показывается ЗДЕСЬ, а тема ставится **до** показа — тем же порядком, каким
    /// это делает съёмка (`Program.Shot`): тема — свойство приложения, и окно, построенное раньше
    /// неё, осталось бы в прежней. И ставится она **тем же решением**, что у съёмки
    /// (<see cref="ShotDecisions.Palette"/>) — тема панели, а не строка ключа.
    /// </summary>
    private static double MeanBrightness(string kind, string themeValue)
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);
        var window = Program.ShotWindowWithDoors(kind, paths, ShotServer(paths)).Window;

        App.ApplyTheme(ShotDecisions.Palette(themeValue)!.Value);
        window.Show();
        PanelTestStand.Settle();

        try
        {
            var width = (int)Math.Ceiling(window.ClientSize.Width);
            var height = (int)Math.Ceiling(window.ClientSize.Height);

            Assert.True(width > 0 && height > 0, $"пустой размер окна {width}x{height}");

            var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);

            var stride = width * 4;
            var buffer = new byte[stride * height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            long sum = 0;
            long count = 0;

            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                if (buffer[i + 3] == 0) continue;

                // Яркость — по среднему каналу: для «темнее/светлее» точность не нужна,
                // а от цветового зрения проверки зависеть не должны.
                sum += (buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3;
                count++;
            }

            Assert.True(count > 0, $"кадр «{(kind.Length == 0 ? "(без имени)" : kind)}»: ни одного непрозрачного пикселя");

            return (double)sum / count;
        }
        finally
        {
            window.Close();
            PanelTestStand.RemoveTemp(dir);

            // Тема возвращается к «как в Windows»: она ОБЩАЯ на процесс, и оставленная тёмной
            // она покрасила бы кадр соседней проверке (классы вида идут в одной коллекции).
            App.ApplyTheme(PanelTheme.System);
        }
    }
}
