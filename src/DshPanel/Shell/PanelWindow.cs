using Avalonia;
using Avalonia.Controls;
using DshPanel.Isolation;

namespace DshPanel.Shell;

/// <summary>
/// Окно панели: показать, спрятать, и «закрытие» означает «спрятать».
///
/// Отдельно от значка в трее — намеренно. Значок живёт на рабочем столе владельца, и
/// поднимать его в проверке нельзя; а поведение окна проверить нужно. Здесь нет ничего,
/// кроме окна и правил, поэтому это проверяется тестами без единого значка.
///
/// Крестик прячет окно, а не завершает панель: панель живёт в трее, и «закрыл окно» у неё
/// не значит «вышел». Завершает только пункт «Выход» (<see cref="CloseForExit"/>).
/// </summary>
public sealed class PanelWindow : IDisposable
{
    private readonly Window _window;
    private readonly bool _isolated;
    private readonly Action<string> _log;

    private bool _closingForExit;

    /// <summary>
    /// Состояние окна, о котором связка уже знает: «свернулось» и «развернулось» поднимаются
    /// на ПЕРЕХОДЕ, а не на каждом изменении свойства. Без этого повторный `Minimized` поднимал
    /// бы прятание второй раз, а `Normal` после `Maximized` — возврат там, где ничего не прятали.
    /// </summary>
    private WindowState _state;

    public PanelWindow(Window window, bool isolated, Action<string> log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isolated = isolated;

        _state = _window.WindowState;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _window.Closing += OnClosing;
    }

    public bool IsVisible => _window.IsVisible;

    /// <summary>
    /// Где окно панели стоит СЕЙЧАС — в пикселях экрана, или <c>null</c>, если панель убрана
    /// в трей (окна не видно) либо ещё не мерилось.
    ///
    /// Нужно ровно одному решению: куда поставить вспомогательное окно (настройки, копии, «Пики
    /// и тарифы», «О программе»). Панель у человека есть — окно встаёт ПОВЕРХ неё, по её центру;
    /// панель убрана в трей — ставить поверх нечего, и окно идёт к курсору
    /// (см. <c>WindowPlacement.NearPanel</c>).
    ///
    /// Перевод DIP в пиксели — ОБЩИЙ с запоминанием места самой панели (<c>WindowPlacement.Frame</c>):
    /// разойдись эти два перевода, окно встало бы не на панели, а рядом с местом, которого она
    /// не занимала.
    ///
    /// ⚠️ **DEF-039: панель при открытии спутника остаётся видимой, и история об этом — словами,
    /// а не цитатой прежней ошибки.** Прежняя запись обещала, что панель при этом закроется, —
    /// код её не прятал ни дня. Здесь важно ровно одно: место панели спрашивают ДЛЯ ТОГО, чтобы
    /// поставить окно поверх неё, и ни один из этих путей не прячет и не закрывает панель.
    /// Проверяет это `WindowsOverPanelTests` (оба окна видимы после постановки).
    /// </summary>
    public PixelRect? ScreenFrame
    {
        get
        {
            if (!_window.IsVisible) return null;

            var size = _window.ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return null;

            return WindowPlacement.Frame(_window.Position, size, _window.RenderScaling);
        }
    }

    /// <summary>Рабочие области всех экранов — в пикселях, как их отдаёт система.</summary>
    public IReadOnlyList<PixelRect> ScreenAreas =>
        _window.Screens.All.Select(screen => screen.WorkingArea).ToArray();

    /// <summary>
    /// Масштаб экрана под точкой. Ноль (Windows не ответила, экран не найден) читается как «как
    /// есть»: размеры окна умножаются на единицу, а не пропадают вовсе.
    /// </summary>
    public double ScaleAt(PixelPoint point)
    {
        var screen = _window.Screens.ScreenFromPoint(point) ?? _window.Screens.Primary;

        return screen?.Scaling is > 0 ? screen.Scaling : 1;
    }

    /// <summary>
    /// Прятать окно по крестику (<c>true</c>, обычное поведение панели в трее) или закрывать
    /// по-настоящему (<c>false</c>).
    ///
    /// Второй случай не выдуман: если значок в трее не встал, панель осталась бы без единого
    /// способа себя закрыть — окно пряталось бы, а выйти было бы нечем. Тогда панель работает
    /// «окном без значка», и закрытие окна её завершает.
    /// </summary>
    public bool HideOnClose { get; set; } = true;

    /// <summary>
    /// Показать панель. Возвращает <c>false</c>, если показывать нельзя.
    ///
    /// Решение принимает предикат изоляции, а не этот код: правило одно на всю панель,
    /// и спрашивать его обязано каждое место, которое что-то показывает человеку.
    /// </summary>
    public bool Show()
    {
        if (!IsolationRules.ShouldShowPanelOnSignal(_isolated))
        {
            // Подавленное решение пишем в журнал: иначе «панель молчала» нечем объяснить.
            _log(PanelStrings.WindowSuppressedLog);
            return false;
        }

        _window.Show();

        // Свёрнутое окно, которое «показали», осталось бы свёрнутым — это выглядит как поломка.
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

        _window.Activate();
        Shown?.Invoke();
        return true;
    }

    public void Hide() => _window.Hide();

    /// <summary>
    /// ПАНЕЛЬ СПРЯТАЛАСЬ В ТРЕЙ — зовётся ПОСЛЕ <c>Hide()</c> и только в этом случае.
    ///
    /// Зачем событие, а не прямой вызов из связки: «уйти в трей» у панели одно место —
    /// <see cref="OnClosing"/> (крестик, Esc-дверь, щелчок по значку), и второго места, где
    /// окно исчезает, быть не должно. Связка вешает сюда прятание спутников (вариант 2 нормы
    /// `95-windows.md` §3.2: владельца нет, и «спрятать вместе» пишем сами).
    /// </summary>
    public Action? Hidden { get; set; }

    /// <summary>
    /// ПАНЕЛЬ ПОКАЗАЛАСЬ (щелчок по значку, кнопка первого запуска) — зовётся ПОСЛЕ <c>Show()</c>.
    /// Сюда связка вешает возврат спутников, спрятанных вместе с панелью: так ведёт себя система
    /// с owned-окнами (`О-6`: «when an owner window is restored, the system automatically shows
    /// the associated owned windows»), и то же поведение человек ждёт от нас.
    /// </summary>
    public Action? Shown { get; set; }

    /// <summary>
    /// ПАНЕЛЬ СВЕРНУЛАСЬ (кнопка «свернуть» на заголовке) — зовётся на ПЕРЕХОДЕ в
    /// <see cref="WindowState.Minimized"/>.
    ///
    /// **Зачем отдельная дверь (DEF-061, 2.4.2).** Спутники панели намеренно БЕЗ владельца
    /// (`Window.Owner`): с владельцем у них появилась бы вторая кнопка на панели задач
    /// (`О-8` — `ShowInTaskbar` прячет кнопку скрытым родителем, а не владением). Цена названа
    /// в банке прямо: «скрытие при минимизации панели надо написать самим (система не сделает
    /// без владельца)» — `_design-library\95-windows.md` §3.2. Норма, из которой это следует, —
    /// `О-6` (Microsoft: owned-окна прячутся при минимизации владельца), и человек ждёт того же
    /// от панели.
    ///
    /// ⚠️ **Это НЕ уход в трей.** Крестик прячет окно совсем (<see cref="Hidden"/>), а «свернуть»
    /// оставляет его на панели задач — и кнопка там обязана остаться ОДНА (`О-7`).
    /// </summary>
    public Action? Minimized { get; set; }

    /// <summary>
    /// ПАНЕЛЬ РАЗВЕРНУЛАСЬ ОБРАТНО — зовётся на ПЕРЕХОДЕ ИЗ <see cref="WindowState.Minimized"/>
    /// в любое другое состояние (кнопка на панели задач, щелчок по значку, «Показать панель»).
    ///
    /// ⚠️ **Не второй показ.** `Show()` уже снимает `Minimized` (см. <see cref="Show"/> выше),
    /// и он же поднимает <see cref="Shown"/>: спутники в этом случае возвращает ЭТА дверь,
    /// а `Shown` получает пустой список и не делает ничего. Так одно действие человека не
    /// поднимает окна дважды — проверяет
    /// `SatelliteMinimizeTests.Возврат_через_показ_панели_не_поднимает_дважды`.
    /// </summary>
    public Action? Restored { get; set; }

    /// <summary>
    /// СМЕНА СОСТОЯНИЯ ОКНА — единственный источник «свернулось»/«развернулось» (DEF-061).
    ///
    /// Реакции на `WindowState.Minimized` до 2.4.2 в панели не было вовсе: спутники прятались
    /// только при уходе в трей, и «свернул панель — настройки висят развёрнутыми» (владелец
    /// воспроизвёл 24.10.2026 на 2.4.1). Слушается САМО свойство, а не событие платформы: так
    /// путь один и для кнопки «свернуть», и для кнопки на панели задач, и для `Show()`.
    /// </summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;

        var now = _window.WindowState;
        if (now == _state) return; // не переход, а повторное присваивание того же значения

        var wasMinimized = _state == WindowState.Minimized;
        _state = now;

        if (now == WindowState.Minimized)
        {
            Minimized?.Invoke();
            return;
        }

        // Вышли из свёрнутого — вернуть то, что спрятали при сворачивании. Переходы между
        // Normal/Maximized/FullScreen спутников не касаются вовсе.
        if (wasMinimized) Restored?.Invoke();
    }

    /// <summary>
    /// Закрыть окно так, как это делает крестик: событие <c>Closing</c> проходит обычный путь
    /// со всеми решениями. Нужно самотесту связки — человека рядом с ним нет.
    /// </summary>
    public void CloseAsUser() => _window.Close();

    /// <summary>Настоящее закрытие — только при выходе панели.</summary>
    public void CloseForExit()
    {
        _closingForExit = true;
        _window.Close();
    }

    /// <summary>
    /// Что сделать ПЕРЕД тем, как окно спрячется или закроется. Сюда связка вешает запоминание
    /// размера и положения: закрытие окна — последнее мгновение, когда оно ещё стоит там, где
    /// его оставил человек, и единственное, которое наступает и по крестику, и по «Выходу».
    ///
    /// Зовётся ДО развилки «прятать или закрывать»: при выключенном <see cref="HideOnClose"/>
    /// окно закрывается по-настоящему, и геометрию надо успеть запомнить так же.
    /// </summary>
    public Action? BeforeClosing { get; set; }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        BeforeClosing?.Invoke();

        if (_closingForExit || !HideOnClose) return;

        e.Cancel = true;
        _window.Hide();
        _log(PanelStrings.WindowHiddenLog);

        // ⚠️ СПУТНИКИ ПРЯЧУТСЯ ВМЕСТЕ С ПАНЕЛЬЮ — ПОСЛЕ прятания, не до: если спрятать их раньше,
        // отмена закрытия панели (или отказ в нём) оставила бы человека без окон, которые он
        // не закрывал. Владельца у них нет (вариант 2), и система этого не сделает сама.
        Hidden?.Invoke();
    }

    public void Dispose()
    {
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _window.Closing -= OnClosing;
    }
}
