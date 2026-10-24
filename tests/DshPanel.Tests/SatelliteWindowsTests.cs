using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ВАРИАНТ 2 НОРМЫ `95-windows.md` §3.2 (решение владельца 22.10.2026) — БЕЗ СИСТЕМНОГО ВЛАДЕЛЬЦА.
///
/// Владельца (`Window.Owner`) у спутников нет: панель живёт в трее, а владелец обязан быть
/// видимым (`О-1`, `О-15`). Поэтому три вещи, которые система с владельцем делает сама, панель
/// делает явно — и каждая обязана проверяться прогоном, а не держаться на двух вызовах
/// в связке, которые однажды снимут:
///
/// 1. **панель ушла в трей — спутники спрятаны** (а не закрыты: человек не выходил);
/// 2. **показ панели возвращает спрятанное**, но НЕ возвращает закрытое человеком;
/// 3. **кнопка панели задач одна**: у спутников `ShowInTaskbar = false` (норма `О-7`), и ставит
///    его ОДНО место — <see cref="PanelShell.Place(Window, PixelRect?, PixelPoint?, System.Collections.Generic.IReadOnlyList{PixelRect}, double)"/>,
///    а не разметка шести окон.
///
/// ⚠️ **Чего эти проверки НЕ доказывают** (`О-17`): z-order и кнопки панели задач — это состояние
/// ОС, а не свойства окна. «Спутник поверх панели» доказывается живой пробой на настоящих окнах
/// (прогон-кадр u3) и тем, что `Topmost` нигде не ставится; «одна кнопка» — свойствами окон,
/// которые её дают (`ShowInTaskbar`).
/// </summary>
public class SatelliteWindowsTests
{
    private static SingleWindowSlot SlotWith(Window window)
    {
        var slot = new SingleWindowSlot();
        slot.Open(() => window, place: _ => { });
        return slot;
    }

    // ------------------------------------------------------------------ (1) спрятать вместе

    /// <summary>
    /// ПАНЕЛЬ УШЛА В ТРЕЙ — СПУТНИКИ СПРЯТАНЫ, А НЕ ЗАКРЫТЫ. Проверяется прямой дверью
    /// <see cref="SatelliteSet.HideAll"/>: она одна и зовётся связкой по событию `PanelWindow.Hidden`.
    ///
    /// Мутация «не прятать спутники» (убрать `_window.Hidden = HideSatellites` или тело
    /// `HideAll`) роняет это утверждение: окна остаются висеть поверх убранной панели.
    /// </summary>
    [AvaloniaFact]
    public void Панель_в_трей_прячет_спутники()
    {
        var settings = new Window { Width = 300, Height = 200 };
        var backups = new Window { Width = 300, Height = 200 };

        var settingsSlot = SlotWith(settings);
        var backupsSlot = SlotWith(backups);

        Assert.True(settings.IsVisible);
        Assert.True(backups.IsVisible);

        var satellites = new SatelliteSet(settingsSlot, backupsSlot);

        Assert.Equal(2, satellites.HideAll());

        Assert.False(settings.IsVisible);
        Assert.False(backups.IsVisible);

        // Спрятать — не закрыть: слоты помнят окна, и закрытие не поднималось.
        Assert.True(settingsSlot.IsOpen);
        Assert.True(backupsSlot.IsOpen);
    }

    /// <summary>
    /// СПРЯТАННОЕ ВОЗВРАЩАЕТСЯ, ЗАКРЫТОЕ — НЕТ. Пока панель была в трее, человек мог закрыть
    /// окно кнопкой закрытия; возвращать закрытое значило бы отменить его действие.
    /// </summary>
    [AvaloniaFact]
    public void Показ_панели_возвращает_спрятанное_но_не_закрытое()
    {
        var settings = new Window { Width = 300, Height = 200 };
        var backups = new Window { Width = 300, Height = 200 };

        var settingsSlot = SlotWith(settings);
        var backupsSlot = SlotWith(backups);

        var satellites = new SatelliteSet(settingsSlot, backupsSlot);

        Assert.Equal(2, satellites.HideAll());

        // Человек закрыл копии, пока панель была в трее.
        backups.Close();
        Assert.False(backupsSlot.IsOpen);

        Assert.Equal(1, satellites.Restore());

        Assert.True(settings.IsVisible);
        Assert.False(backups.IsVisible);

        // Второй показ панели ничего не «возвращает»: список уже пуст.
        Assert.Equal(0, satellites.Restore());
    }

    /// <summary>
    /// ПОВТОРНАЯ ПРОСЬБА ДВЕРИ ПОКАЗЫВАЕТ СПРЯТАННОЕ ОКНО (норма `О-10`). Прежде окно звалось
    /// только `Activate()`, и на скрытом окне это не показывало НИЧЕГО: «нажимаю „Настройки…“ —
    /// ничего не происходит». Мутация «убрать Show() у скрытого окна» роняет эту проверку.
    /// </summary>
    [AvaloniaFact]
    public void Повторная_просьба_показывает_спрятанное_окно()
    {
        var settings = new Window { Width = 300, Height = 200 };

        var raised = new List<Window>();
        var slot = new SingleWindowSlot { Raise = raised.Add };
        slot.Open(() => settings, place: _ => { });

        settings.Hide();
        Assert.False(slot.IsVisible);

        var built = slot.Open(() => throw new InvalidOperationException("второе окно строить нельзя"));

        Assert.False(built);
        Assert.True(settings.IsVisible);

        // Показанное по повторной просьбе окно ещё и ПОДНИМАЕТСЯ: иначе оно появилось бы
        // под панелью, и человек снова не увидел бы его.
        Assert.Equal(2, raised.Count);
        Assert.All(raised, window => Assert.Same(settings, window));
    }

    /// <summary>
    /// УХОД В ТРЕЙ ПОДНИМАЕТ СОБЫТИЕ, НАСТОЯЩЕЕ ЗАКРЫТИЕ — НЕТ. На этом событии и держится
    /// прятание спутников: связка вешает на него <see cref="SatelliteSet.HideAll"/>, и второго
    /// места, где окно панели исчезает, быть не должно.
    ///
    /// Мутация «не поднимать событие» (убрать `Hidden?.Invoke()`) роняет проверку: спутники
    /// остались бы на экране поверх убранной панели — ровно то, что было до 2.4.0.
    /// </summary>
    [AvaloniaFact]
    public void Уход_в_трей_поднимает_событие_а_настоящее_закрытие_нет()
    {
        var panel = new Window { Width = 200, Height = 200 };
        panel.Show();

        var window = new PanelWindow(panel, isolated: false, log: _ => { });

        var hidden = 0;
        var shown = 0;

        window.Hidden = () => hidden++;
        window.Shown = () => shown++;

        // Крестик: панель прячется в трей — событие есть.
        window.CloseAsUser();

        Assert.Equal(1, hidden);
        Assert.False(panel.IsVisible);

        // Показ панели — событие возврата.
        window.Show();
        Assert.Equal(1, shown);

        // «Выход»: окно закрывается по-настоящему — прятать нечего, событие не поднимается.
        window.CloseForExit();
        Assert.Equal(1, hidden);
    }

    // ------------------------------------------------------------------ (2) одна кнопка панели задач

    /// <summary>
    /// КНОПКА ПАНЕЛИ ЗАДАЧ ОДНА: у спутника `ShowInTaskbar = false`, и ставит это ОДНО место —
    /// <see cref="PanelShell.Place(Window, PixelRect?, PixelPoint?, System.Collections.Generic.IReadOnlyList{PixelRect}, double)"/>,
    /// через которое проходят все шесть вспомогательных окон.
    ///
    /// **Мутация «не убирать кнопку»** (убрать `window.ShowInTaskbar = false`) роняет проверку:
    /// у окна остаётся умолчание Avalonia `true`, и на панели задач появляется вторая кнопка —
    /// ровно то, что было в 2.3.2 (пять кнопок по коду).
    /// </summary>
    [AvaloniaFact]
    public void Спутник_не_даёт_второй_кнопки_на_панели_задач()
    {
        var satellite = new Window { Width = 300, Height = 200 };

        // Умолчание Avalonia — кнопка ЕСТЬ; этим проверка и отличается от пустого утверждения.
        Assert.True(satellite.ShowInTaskbar);

        PanelShell.Place(satellite, panel: null, cursor: null, screens: Array.Empty<PixelRect>(), scale: 1);

        Assert.False(satellite.ShowInTaskbar);

        // `Topmost` не выставляется нигде (норма `О-12`): «поверх» достигается подъёмом, а не
        // перебиванием чужих окон.
        Assert.False(satellite.Topmost);

        // Место по-прежнему ставится явно, до показа (`О-9`) — правило, которое было и осталось.
        Assert.Equal(WindowStartupLocation.Manual, satellite.WindowStartupLocation);
    }

    // ------------------------------------------------------------------ (3) спутник поверх панели

    /// <summary>
    /// СПУТНИК ПОДНИМАЕТСЯ НАД ПАНЕЛЬЮ ПОДЪЁМОМ, А НЕ `Topmost` (норма `О-12`): после открытия
    /// окно активно, панель — нет. Это и есть «поверх» в терминах свойств; настоящий z-order
    /// проверяется живой пробой (кадр u3) — кадром изнутри прогона его не видно (`О-17`).
    ///
    /// Мутация «не поднимать окно» (убрать `Activate()` в `SingleWindowSlot.Open`) роняет
    /// утверждение «окно активно после двери».
    /// </summary>
    [AvaloniaFact]
    public void Спутник_поднимается_над_панелью()
    {
        var panel = new Window { Width = 200, Height = 200 };
        panel.Show();

        var settings = new Window { Width = 300, Height = 200 };

        // Подъём в безоконном прогоне не виден (`IsActive` остаётся ложным) — поэтому смотрим,
        // ЧТО именно зовёт дверь: подъём, а не `Topmost` и не «показ без подъёма».
        var raised = new List<Window>();
        var slot = new SingleWindowSlot { Raise = raised.Add };
        slot.Open(() => settings, place: _ => { });

        Assert.True(settings.IsVisible);
        Assert.Same(settings, Assert.Single(raised));

        // «Поверх» достигается подъёмом, а не перебиванием чужих окон (норма О-12).
        Assert.False(settings.Topmost);
        Assert.False(panel.Topmost);

        // И панель при этом не поднимали: она осталась ниже окна, а не наоборот.
        Assert.DoesNotContain(panel, raised);
    }
}
