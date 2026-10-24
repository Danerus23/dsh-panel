using DshPanel.Isolation;
using DshPanel.Tray;

namespace DshPanel.Shell;

/// <summary>
/// КУДА ВЕДЁТ ЩЕЛЧОК ПО ШАРИКУ. Перечисление, а не условие на месте: цель сообщения — это
/// РЕШЕНИЕ о поведении, и она обязана проверяться перебором всех видов, не открывая ни значка,
/// ни окна, ни рабочего стола владельца.
///
/// <see cref="None"/> — «двери нет»: у такого сообщения нет окна, о котором оно говорит, и
/// щелчок обязан не делать НИЧЕГО (банк `60-states.md` §5а: «клик по шарику — дверь или ничего»;
/// шарик не должен обещать действие, которого нет).
/// </summary>
public enum NoticeTarget
{
    /// <summary>Двери нет: факт живёт в журнале, открывать нечего.</summary>
    None,

    /// <summary>Главное окно панели.</summary>
    Panel,

    /// <summary>Окно настроек: там живёт автозапуск — единственное сообщение с этой дверью.</summary>
    Settings,

    /// <summary>Раздел «Обновление» окна настроек — там же, где полоса подготовки (единственная в панели).</summary>
    UpdateSection,

    /// <summary>Окно копий и восстановления — там же, где итог и причина отказа копии.</summary>
    Backups,

    /// <summary>Окно «Пики и тарифы».</summary>
    Peaks,

    /// <summary>Окно истории цен.</summary>
    PricingHistory,
}

/// <summary>
/// КАКОЕ ОКНО ОТКРЫВАЕТ ШАРИК. Чистая функция от вида сообщения: одно место, где сказано,
/// куда ведёт каждое из 12 сообщений панели (`Isolation\NoticeKind.cs`). Второго такого места
/// быть не должно — разойдясь, они открывали бы по одному шарику два разных окна.
///
/// **Правило выбора простое: ведём туда, где этот факт виден человеку.** У обновления — раздел
/// «Обновление»: там единственная полоса панели и ход подготовки (`60-states.md` §2а), и туда же
/// ведёт шарик «идёт скачивание» — процентов в шарике нет (решение капитана 24.10.2026, названное
/// отступление от формулировки брифа «прогресс в шарике»). У сломанного автозапуска — настройки,
/// раздел «Общие»: автозапуск включается там (поправка дизайн-лидера 24.10.2026).
///
/// ⚠️ **Карта НАРОЧНО узкая — только виды u2.** У остальных сообщений двери в окна — предмет
/// своих задач (сервер «упал сам» — u10; копии, пики и цены — свои окна и свои задачи), и
/// открывать их отсюда значило бы завести второе место, где решается, куда ведёт сообщение.
/// Для таких видов щелчок НИЧЕГО не открывает и пишет строку в журнал: «дверь или ничего»
/// (банк `60-states.md` §5а) соблюдено, а обещания действия нет.
/// </summary>
public static class NoticeTargets
{
    /// <summary>Куда ведёт щелчок по шарику этого вида.</summary>
    public static NoticeTarget Of(NoticeKind kind) => kind switch
    {
        NoticeKind.UpdatePreparing => NoticeTarget.UpdateSection,
        NoticeKind.UpdateAvailable => NoticeTarget.UpdateSection,
        NoticeKind.UpdateOutcome => NoticeTarget.UpdateSection,

        NoticeKind.AutostartBroken => NoticeTarget.Settings,

        _ => NoticeTarget.None,
    };

    /// <summary>
    /// СЛОВО ЦЕЛИ ДЛЯ ЖУРНАЛА: по нему видно, куда ушёл щелчок, не открывая окно. Журнал
    /// технический (отступление №4), поэтому это значения, а не фраза для человека.
    /// </summary>
    public static string Name(NoticeTarget target) => target switch
    {
        NoticeTarget.Panel => "panel",
        NoticeTarget.Settings => "settings",
        NoticeTarget.UpdateSection => "update",
        NoticeTarget.Backups => "backups",
        NoticeTarget.Peaks => "peaks",
        NoticeTarget.PricingHistory => "pricing-history",
        _ => "none",
    };
}

/// <summary>
/// ДВЕРЬ ЩЕЛЧКА ПО ШАРИКУ (DEF-046, 2.4.0). Событие `TrayIconHost.NotificationClicked` объявлено
/// и поднимается с самого начала, но подписчиков у него было **ноль**: человек щёлкал по
/// уведомлению — и не происходило ничего. Здесь подписка и живёт: одна на процесс, рядом со
/// значком, потому что второго пути к шарику в панели нет.
///
/// **Что важно в устройстве, и почему именно так.**
///
/// 1. **Помнится ТОЛЬКО показанное сообщение.** `Remember` зовёт общая дверь показа
///    (`App.Notify`) после успешного показа, `Forget` — когда показать не удалось. Иначе щелчок
///    по пустому месту открывал бы окно «ни с того ни с сего», а в прогоне проверки (где шарики
///    молчат по изоляции) — ещё и мешал бы человеку.
/// 2. **«Дверь или ничего».** Вид без окна (`NoticeTarget.None`) и отсутствие связки — это не
///    ошибка, а честный ответ: строка в журнал, окно не открывается. Обещать действие, которого
///    нет, банк запрещает прямо.
/// 3. **Одно событие — одна цель.** Помнится последнее сообщение: два шарика подряд не должны
///    открывать два окна по одному щелчку.
///
/// ⚠️ **Чего эта дверь не делает.** Не показывает окно сама: открытие — дело связки
/// (`PanelShell`), у которой слоты окон и права прогона. Дверь только решает «куда» и говорит
/// об отказе строкой в журнал.
/// </summary>
public sealed class NoticeClickDoor : IDisposable
{
    private readonly TrayIconHost _tray;
    private readonly Func<NoticeTarget, bool> _open;
    private readonly Action<string> _log;

    private NoticeKind? _shown;
    private bool _disposed;

    /// <param name="tray">Значок, у которого есть событие щелчка по уведомлению.</param>
    /// <param name="open">Дверь связки: открыть окно и ответить, открылось ли.</param>
    /// <param name="log">Журнал панели: сюда уходят исход щелчка и причина отказа.</param>
    public NoticeClickDoor(TrayIconHost tray, Func<NoticeTarget, bool> open, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(log);

        _tray = tray;
        _open = open;
        _log = log;

        _tray.NotificationClicked += OnClick;
    }

    /// <summary>
    /// ПОДПИСКА — ОТДЕЛЬНЫМ МЕТОДОМ, а не самим <see cref="Click"/>: у события <c>Action</c>,
    /// а щелчок отвечает «открыли или нет» — этим ответом пользуется прогон. Один и тот же
    /// метод и подписывается, и снимается, иначе уборка не снимет подписку.
    /// </summary>
    private void OnClick() => Click();

    /// <summary>Куда поведёт щелчок сейчас: <see cref="NoticeTarget.None"/> — он ничего не откроет.</summary>
    public NoticeTarget Target => _shown is { } kind ? NoticeTargets.Of(kind) : NoticeTarget.None;

    /// <summary>Показанное сообщение: с этого мгновения щелчок ведёт в его окно.</summary>
    public void Remember(NoticeKind kind) => _shown = kind;

    /// <summary>Сообщение не показано: щелчку некуда вести (и «показано» не было — не врём).</summary>
    public void Forget() => _shown = null;

    /// <summary>
    /// ЩЕЛЧОК. Возвращает «открыли» — им пользуется прогон, и он же честен для журнала:
    /// отказ не прячется.
    /// </summary>
    internal bool Click()
    {
        // ⚠️ БЕЗ ПОКАЗАННОГО СООБЩЕНИЯ КЛИКА НЕ БЫВАЕТ: шарика на экране нет — щёлкать не по чему.
        // Строку об этом НЕ пишем (поправка дизайн-лидера 24.10.2026): причину отказа показа уже
        // назвала общая дверь (`PanelStrings.NotifyFailedLogFormat`), второй такой строки не надо.
        if (_shown is not { } kind) return false;

        var target = NoticeTargets.Of(kind);

        if (target == NoticeTarget.None)
        {
            // Дверь или ничего: у такого вида нет окна, и щелчок не выдумывает его. Строка
            // остаётся в журнале — по ней видно, что человек щёлкал, а окно не открылось.
            _log("щелчок по шарику: у сообщения нет окна — остаётся журнал (" + kind + ")");
            return false;
        }

        var opened = false;

        try
        {
            opened = _open(target);
        }
        catch (Exception error)
        {
            // Дверь связки может бросить (окно не построилось, платформа ушла): панель от этого
            // падать не должна — щелчок по шарику не стоит жизни процессу.
            _log("щелчок по шарику: открыть не удалось — " + error.GetType().Name + ": " + error.Message);
            return false;
        }

        _log(opened
            ? "щелчок по шарику: открыт " + NoticeTargets.Name(target) + " (" + kind + ")"
            : "щелчок по шарику: открыть нечем — " + NoticeTargets.Name(target) + " (" + kind + ")");

        return opened;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tray.NotificationClicked -= OnClick;
        _shown = null;
    }
}
