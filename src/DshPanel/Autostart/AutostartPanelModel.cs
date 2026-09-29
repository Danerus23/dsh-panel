using DshPanel.Shell;

namespace DshPanel.Autostart;

/// <summary>
/// Что окно панели показывает про автозапуск и что разрешает нажать.
///
/// Отдельно от окна и без единой ссылки на Avalonia — намеренно: именно это и проверяется
/// тестами без экрана. Здесь же живёт правило, которое легко потерять: галочка отвечает на
/// вопрос «запись есть?», а КУДА она ведёт — это <see cref="AutostartState"/>: запись может
/// вести на другую копию панели, и человек обязан это видеть, а не догадываться.
/// </summary>
public sealed class AutostartPanelModel
{
    private readonly IAutostartControl? _control;

    /// <summary>
    /// Последняя попытка изменить запись кончилась отказом. Снимается <see cref="Refresh"/>:
    /// отказ говорит про ПОПЫТКУ, а не про состояние записи, и залипшим ему быть нельзя.
    /// </summary>
    private bool _changeFailed;

    public AutostartPanelModel(IAutostartControl? control = null) => _control = control;

    public AutostartState State => _control?.State ?? AutostartState.UnknownState;

    public bool Enabled => _control is not null && State.Enabled;

    /// <summary>
    /// Менять запись можно только у настоящего прогона: в прогоне проверки хранилище
    /// «только на чтение», и галочка обязана быть недоступной — иначе она обещала бы действие,
    /// которого не будет.
    /// </summary>
    public bool CanToggle => _control is not null && _control.Writable && State.Known;

    public string CheckText => PanelStrings.AutostartCheck;

    /// <summary>
    /// Попытка включить или выключить автозапуск этой копии. Возвращает то, что вышло НА САМОМ ДЕЛЕ.
    ///
    /// Отказ запоминается здесь, а не только уходит строкой в журнал (см. <see cref="NoteText"/>):
    /// галочка после неудачи возвращается в прежнее положение, и без объяснения человек видит
    /// «щелчок — и ничего». Это ровно та беда, из-за которой перестают верить и остальным
    /// сообщениям панели, поэтому отказ обязан быть ВИДЕН в окне.
    ///
    /// Запись не меняется и «отказом» не объявляется там, где менять её нельзя
    /// (<see cref="CanToggle"/> — прогон проверки): подпись про отказ была бы ложью.
    /// </summary>
    public bool Set(bool enabled)
    {
        if (!CanToggle) return false;

        _changeFailed = !_control!.Set(enabled);
        return !_changeFailed;
    }

    /// <summary>
    /// Перечитать запись. Отказ при этом снимается: после сверки подпись снова говорит о том,
    /// что стоит на самом деле, — иначе «не удалось» пережило бы и удачную попытку.
    /// </summary>
    public void Refresh()
    {
        _changeFailed = false;
        _control?.Refresh();
    }

    public string NoteText
    {
        get
        {
            if (_control is null) return PanelStrings.AutostartUnbound;

            // Отказ важнее рассказа о том, что стоит сейчас: человек только что нажал галочку,
            // и ему нужен ответ на «что не вышло», а не описание прежнего состояния.
            if (_changeFailed) return PanelStrings.AutostartFailed;

            var text = State.Where switch
            {
                AutostartWhere.Self => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture, PanelStrings.AutostartSelfFormat, State.Path),
                AutostartWhere.Missing => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture, PanelStrings.AutostartMissingFormat, State.Path),
                AutostartWhere.Other => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture, PanelStrings.AutostartOtherFormat, State.Path),
                AutostartWhere.Unknown => PanelStrings.AutostartUnknown,
                _ => PanelStrings.AutostartOff,
            };

            return _control.Writable ? text : text + PanelStrings.AutostartLockedSuffix;
        }
    }
}
