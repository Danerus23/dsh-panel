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

    public PanelWindow(Window window, bool isolated, Action<string> log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isolated = isolated;

        _window.Closing += OnClosing;
    }

    public bool IsVisible => _window.IsVisible;

    /// <summary>
    /// Где окно панели стоит СЕЙЧАС — в пикселях экрана, или <c>null</c>, если панель убрана
    /// в трей (окна не видно) либо ещё не мерилось.
    ///
    /// Нужно ровно одному решению: куда поставить вспомогательное окно (настройки, копии, «Пики
    /// и тарифы», «О программе»). Панель у человека есть — окно встаёт рядом с ней; панель убрана
    /// в трей — рядом ставить не с чем, и окно идёт к курсору (см. <c>WindowPlacement.NearPanel</c>).
    ///
    /// Перевод DIP в пиксели — ОБЩИЙ с запоминанием места самой панели (<c>WindowPlacement.Frame</c>):
    /// разойдись эти два перевода, окно встало бы «рядом» с местом, которого панель не занимала.
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
        return true;
    }

    public void Hide() => _window.Hide();

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
    }

    public void Dispose() => _window.Closing -= OnClosing;
}
