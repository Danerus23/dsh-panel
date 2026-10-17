using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// РАЗДЕЛ «СЕРВЕР» ПОКАЗЫВАЕТ ПРАВДУ САМ — БЕЗ НАЖАТИЯ КНОПКИ (задание владельца 14.10.2026, §3).
///
/// **От какого случая.** Владелец открыл раздел «Сервер», увидел «кучу красных» меток и решил, что
/// что-то сломано. Оказалось, надо нажать «Проверить окружение»; его слова: *«Я думал панель
/// автоматически это делает»*. Решение его же: **вид не менять — чинить механику**.
///
/// **Почему так выходило (замер по коду).** Живая проба умений идёт В ФОНЕ
/// (<see cref="LiveSkillProbe"/>) — сеть в перерисовке окна держать нельзя, — но о своём
/// результате она НИКОМУ не сообщала; смена состояния сервера раздел тоже не замечал. Раздел
/// рисовался один раз (при открытии) и по нажатию кнопки, и до него это и было единственным
/// способом увидеть правду.
///
/// ⚠️ **Ни один из трёх случаев не проверяется кадром, и это осознанно:** вид раздела не меняется
/// (задание — только про механику), а «перерисовалось само» кадром не докажешь — доказательство
/// здесь ПРОГОН: было так, пришли данные, стало иначе БЕЗ нажатия.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class ServerSectionLiveTests
{
    /// <summary>
    /// СТЕНД: настоящее окно настроек с НАСТОЯЩИМ контроллером под своим временным корнем —
    /// иначе проверялся бы не тот путь, каким идёт человек (сообщение идёт проба → дверь →
    /// контроллер → окно, и подмени в цепочке любое звено — проверка ничего не докажет).
    /// </summary>
    private static (SettingsWindow Window, SettingsController Settings, string Dir) Stand(
        IServerControl? server = null)
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);

        var controller = new SettingsController(
            new SettingsStore(paths.SettingsFile),
            paths,
            allowed: true,
            canReadOwnerEnvironment: false,
            locateEngine: () => null,
            server: server is null ? null : () => server,
            applyTheme: _ => { },
            log: _ => { });

        var window = new SettingsWindow();
        window.Attach(controller, autostart: null);
        window.ShowSection(SettingsWindow.ServerSection);
        window.Show();
        PanelTestStand.Settle();

        return (window, controller, dir);
    }

    /// <summary>ЧТО НАРИСОВАНО В РАЗДЕЛЕ — текстом строк, а не разбором: проверяется вид.</summary>
    private static List<string> Drawn(SettingsWindow window)
    {
        var panel = window.FindControl<StackPanel>("EnvironmentPanel");

        Assert.NotNull(panel);

        return panel!.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text ?? string.Empty)
            .Where(text => text.Length > 0)
            .ToList();
    }

    /// <summary>Факты пробы «здесь и сейчас» — те же, что отдаёт настоящая проба после удачи.</summary>
    private static SkillFacts Probed(AppPaths paths, DateTimeOffset at) =>
        EngineSkillProbe.None(paths) with { MeasuredAt = at };

    /// <summary>
    /// ФАКТЫ ПРОБЫ ДОЕЗЖАЮТ ДО РАЗДЕЛА САМИ — РУКАМИ НИЧЕГО НАЖИМАТЬ НЕ НАДО.
    ///
    /// Раздел нарисован, проба молчит (фактов нет) — и так и говорит: «не проверялась». Потом
    /// проба отдаёт факты (ровно то, что она делает в фоне, закончив работу) — и раздел обязан
    /// показать ИХ, без кнопки и без переоткрытия окна.
    ///
    /// Мутация, которая обязана упасть здесь: **снять подписку окна на сообщение о данных**
    /// (`_settings.EnvironmentChanged += RenderEnvironment` убрана) — раздел остаётся прежним.
    /// </summary>
    [AvaloniaFact]
    public void Факты_пробы_доезжают_до_раздела_без_нажатия_кнопки()
    {
        var (window, settings, dir) = Stand();

        try
        {
            var paths = AppPaths.Under(dir);
            var at = new DateTimeOffset(2026, 10, 14, 18, 30, 0, TimeSpan.FromHours(3));

            var facts = new SkillFacts();

            // ДВЕРЬ ПРОБЫ — такая же, как у настоящей: факты берутся В МОМЕНТ СПРОСА (иначе
            // проверка доказывала бы «раздел перерисовался», а не «раздел показал НОВЫЕ данные»).
            var door = new SkillProbeDoor(() => facts, () => { });
            settings.SkillProbe = door;
            door.Signal();
            PanelTestStand.Settle();

            var before = Drawn(window);

            // ⚠️ Точку с запятой строка раздела снимает: этим же значением собирается СВОДКА
            // отчёта пробы, и «;» там разделитель (`SkillPhrases.Moment` + `TrimEnd`). Берём
            // ту же форму, какую показывает раздел, — иначе проверка ловила бы свою же описку.
            var notAsked = Loc.T(SkillKeys.MomentNotAsked).TrimEnd(';');

            Assert.Contains(before, text => text.Contains(notAsked, StringComparison.Ordinal));
            Assert.DoesNotContain(before, text => text.Contains(Readable(at), StringComparison.Ordinal));

            // ФАКТЫ ПРИШЛИ — и раздел обязан показать их САМ.
            facts = Probed(paths, at);
            door.Signal();
            PanelTestStand.Settle();

            var after = Drawn(window);

            Assert.NotEqual(before, after);
            Assert.Contains(after, text => text.Contains(Readable(at), StringComparison.Ordinal));
            Assert.DoesNotContain(after, text => text.Contains(notAsked, StringComparison.Ordinal));

            // И ПРАВДА ПРИШЛА ИМЕННО ОТ ПРОБЫ: строка «Живая проба умений» помечена зелёным
            // «проверено», а не осталась серой. Слово метки берётся из словаря — как в разделе.
            Assert.Contains(after, text => text == Loc.T(SkillKeys.Checked));
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПЕРЕРИСОВКА РАЗДЕЛА НЕ СТИРАЕТ НЕСОХРАНЁННУЮ ПРАВКУ.
    ///
    /// **От какого случая.** Соблазн на сообщение о данных позвать `Render()` — «он же всё
    /// перерисует». Но `Render()` перечитывает ПОЛЯ из настроек и стёр бы то, что человек набрал
    /// и ещё не сохранил, — ровно в тот миг, когда пришли факты пробы. Тот же довод уже записан
    /// для раздела «Обновление»; здесь он проверяется прогоном.
    ///
    /// Мутация, которая обязана упасть здесь: **звать `Render()` вместо `RenderEnvironment()`**.
    /// </summary>
    [AvaloniaFact]
    public void Перерисовка_раздела_не_стирает_несохранённую_правку()
    {
        var (window, settings, dir) = Stand();

        try
        {
            var paths = AppPaths.Under(dir);
            var box = window.FindControl<TextBox>("WorkDirBox");

            Assert.NotNull(box);

            // ЧЕЛОВЕК НАБРАЛ И НЕ СОХРАНИЛ.
            const string typed = @"D:\Несохранённая папка";
            box!.Text = typed;
            PanelTestStand.Settle();

            // ПРИХОДЯТ ФАКТЫ ПРОБЫ — и правка обязана остаться на месте.
            var facts = new SkillFacts();
            var door = new SkillProbeDoor(() => facts, () => { });
            settings.SkillProbe = door;

            facts = Probed(paths, new DateTimeOffset(2026, 10, 14, 18, 30, 0, TimeSpan.FromHours(3)));
            door.Signal();
            PanelTestStand.Settle();

            Assert.Equal(typed, box.Text);

            // И РАЗДЕЛ ПРИ ЭТОМ ПЕРЕРИСОВАЛСЯ — иначе проверка прошла бы и на мёртвой подписке.
            Assert.Contains(
                Drawn(window),
                text => text.Contains(Readable(new DateTimeOffset(2026, 10, 14, 18, 30, 0, TimeSpan.FromHours(3))), StringComparison.Ordinal));
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// РАЗДЕЛ СЛЕДИТ И ЗА СОСТОЯНИЕМ СЕРВЕРА — второй источник того же сообщения.
    ///
    /// Пока сервер поднимается или гаснет, строки порта и состояния устаревают молча: раздел читал
    /// состояние только в миг отрисовки. Теперь контроллер сервера СООБЩАЕТ о настоящей смене,
    /// и раздел перерисовывается.
    ///
    /// Мутация, которая обязана упасть здесь: **убрать второй источник**
    /// (`WatchServer()` в `SettingsController` не подписывается на смену состояния).
    /// </summary>
    [AvaloniaFact]
    public void Раздел_следит_за_состоянием_сервера()
    {
        var server = new PanelTestStand.StubServer(ServerState.Stopped(3081), ServerOwner.Panel);
        var (window, _, dir) = Stand(server);

        try
        {
            var before = Drawn(window);

            // СЕРВЕР ПОДНЯТ — тем же сообщением, каким о настоящей смене говорит контроллер сервера.
            server.State = new ServerState(ServerPresence.Running, 3081, 4242, "node", "сервер отвечает");
            server.Signal();
            PanelTestStand.Settle();

            var after = Drawn(window);

            Assert.NotEqual(before, after);
            Assert.Contains(before, text => text.Contains(PanelStrings.EnvPortFree, StringComparison.Ordinal));
            Assert.Contains(after, text => text.Contains(PanelStrings.EnvPortOwn, StringComparison.Ordinal));

            // И ПОГАШЕН — тоже сам: сообщение то же, а строка обязана вернуться к «свободен».
            server.State = ServerState.Stopped(3081);
            server.Signal();
            PanelTestStand.Settle();

            var back = Drawn(window);

            Assert.Contains(back, text => text.Contains(PanelStrings.EnvPortFree, StringComparison.Ordinal));
            Assert.DoesNotContain(back, text => text.Contains(PanelStrings.EnvPortOwn, StringComparison.Ordinal));
        }
        finally
        {
            window.CloseQuietly();
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>Момент пробы в читаемом виде — ТОЙ ЖЕ дверью, что показывает раздел.</summary>
    private static string Readable(DateTimeOffset at) =>
        PricingDecisions.CheckedText(PricingDecisions.Stamp(at));
}
