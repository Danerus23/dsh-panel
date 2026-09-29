using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Autostart;

/// <summary>Что окно знает про автозапуск и что ему разрешено.</summary>
public interface IAutostartControl
{
    AutostartState State { get; }

    /// <summary>Можно ли менять запись. В прогоне проверки — нет (см. <see cref="IAutostartStore.Writable"/>).</summary>
    bool Writable { get; }

    void Refresh();

    /// <summary>Включить или выключить автозапуск ЭТОЙ копии. Возвращает то, что вышло на самом деле.</summary>
    bool Set(bool enabled);
}

/// <summary>
/// Автозапуск панели: чтение записи, её изменение по просьбе человека и СВЕРКА при старте.
///
/// Сверка при старте — не украшение, а прямой урок v1: запись хранит абсолютный путь, копий
/// панели на машине бывает несколько, а папку переносят. Панель, которая запись не сверяет,
/// после входа в Windows поднимает чужую (обычно старую) копию и при этом показывает
/// «автозапуск включён». Именно это и случилось у владельца в v1.
///
/// Чего контроллер НЕ делает: не показывает сообщений и не трогает запись в прогоне проверки.
/// Каждое подавленное решение уходит строкой в журнал — иначе «панель молчала» ничем не объяснить.
/// </summary>
public sealed class AutostartController : IAutostartControl
{
    private readonly IAutostartStore _store;
    private readonly string _selfPath;
    private readonly Action<string> _log;

    public AutostartController(IAutostartStore store, string selfPath, Action<string> log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _selfPath = selfPath ?? string.Empty;
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Refresh();
    }

    public AutostartState State { get; private set; } = AutostartState.UnknownState;

    public bool Writable => _store.Writable;

    /// <summary>Где лежит запись — для журнала и отчёта проверки.</summary>
    public string StoreDescription => _store.Describe;

    public void Refresh()
    {
        var record = _store.Read(AutostartDecisions.ValueName);
        State = AutostartDecisions.Classify(record, _selfPath, File.Exists);
    }

    /// <summary>
    /// Включить или выключить автозапуск. Успехом считается не «запись прошла без ошибки»,
    /// а СОСТОЯНИЕ ПОСЛЕ НЕЁ: реестр может быть заперт политикой, и тогда человеку надо
    /// сказать «не вышло», а не показать включённую галочку.
    /// </summary>
    public bool Set(bool enabled)
    {
        if (!_store.Writable)
        {
            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.AutostartLockedLogFormat, _store.Describe));
            return false;
        }

        var wrote = enabled
            ? _store.Write(AutostartDecisions.ValueName, AutostartDecisions.BuildValue(_selfPath))
            : _store.Delete(AutostartDecisions.ValueName);

        Refresh();

        var reached = enabled
            ? State.Where == AutostartWhere.Self
            : !State.Enabled;

        if (wrote && reached)
        {
            _log(enabled ? PanelStrings.AutostartOnLog : PanelStrings.AutostartOffLog);
            return true;
        }

        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.AutostartFailedLogFormat, _store.Describe));
        return false;
    }

    /// <summary>
    /// Сверка записи с этой копией при старте панели. Возвращает «переписали ли».
    ///
    /// Правила ровно два, и оба — про свою же запись: ведёт на исчезнувший файл; либо ведёт
    /// на нас, но записана не нашим значением. Живая чужая копия не трогается никогда:
    /// это законная ситуация, и решает тут человек, а не панель.
    /// </summary>
    public bool Repair()
    {
        Refresh();

        if (!_store.Writable)
        {
            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.AutostartLockedLogFormat, _store.Describe));
            return false;
        }

        if (!AutostartDecisions.ShouldRewrite(State, _selfPath))
        {
            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.AutostartKeptLogFormat, Describe(State)));
            return false;
        }

        var before = Describe(State);
        _store.Write(AutostartDecisions.ValueName, AutostartDecisions.BuildValue(_selfPath));
        Refresh();

        if (State.Where == AutostartWhere.Self)
        {
            _log(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.AutostartRepairedLogFormat, before));
            return true;
        }

        _log(string.Format(
            CultureInfo.CurrentCulture, PanelStrings.AutostartFailedLogFormat, _store.Describe));
        return false;
    }

    /// <summary>Короткое описание состояния по-русски — для журнала. Одно на всю панель.</summary>
    public static string Describe(AutostartState state) => state.Where switch
    {
        AutostartWhere.Self => PanelStrings.AutostartWhereSelfLog,
        AutostartWhere.Missing => string.Format(
            CultureInfo.CurrentCulture, PanelStrings.AutostartWhereMissingLogFormat, state.Path),
        AutostartWhere.Other => string.Format(
            CultureInfo.CurrentCulture, PanelStrings.AutostartWhereOtherLogFormat, state.Path),
        AutostartWhere.Unknown => PanelStrings.AutostartWhereUnknownLog,
        _ => PanelStrings.AutostartWhereOffLog,
    };
}
