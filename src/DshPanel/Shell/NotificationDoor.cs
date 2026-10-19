using System;
using System.Globalization;
using DshPanel.Localization;

namespace DshPanel.Shell;

/// <summary>
/// ОДНА ДВЕРЬ ПОКАЗА АВТОМАТИЧЕСКИХ СООБЩЕНИЙ (2.2.1): исключение из показа не убивает панель,
/// а уходит строкой в журнал.
///
/// **Почему дверь, а не `try` вокруг одного места.** До 2.2.1 <see cref="App.Notify"/> звал
/// <c>TrayIconHost.ShowNotification</c> без защиты. Та бросает в двух случаях —
/// `InvalidOperationException` («значок ещё не поставлен: сначала Show()») и `Win32Exception`
/// («оболочка отказалась показать уведомление»), — и это исключение приходило на нитку интерфейса
/// из <c>ContinueWith</c> фоновой копии. Глобальных обработчиков в панели нет (0 мест), поэтому
/// процесс умирал с `0xe0434352`; в чистой ВМ так и падало (разбор — <c>_team\220\defekt-start-crash.md</c>).
///
/// Точечная обёртка спасала бы только один путь: следующее уведомление с НОВЫМ исключением снова
/// убило бы панель. Поэтому защита стоит на ГРАНИЦЕ — здесь, и через неё проходят все уведомления
/// (<see cref="App.Notify"/> — единственная дверь показа; её зовут копии по расписанию, баланс,
/// пики, тарифы, обновление и «панель запущена»).
///
/// **Молчать нельзя.** Отказ называется в журнале панели: вид сообщения, тип исключения и его текст
/// (<see cref="PanelStrings.NotifyFailedLogFormat"/>). Строка «уведомление показано» пишется только
/// после успешного показа — до 2.2.1 она писалась ДО вызова и врала при разборе.
///
/// **Чего эта дверь НЕ делает:** не подменяет собой последний рубеж (глобальные обработчики
/// исключений) и не глотает исключения молча — причина уходит в журнал, а вызывающий получает
/// <c>false</c>, то есть правду «не показано».
/// </summary>
internal static class NotificationDoor
{
    /// <summary>
    /// Показать сообщение и СКАЗАТЬ в журнале, чем кончилось: показано или нет (и почему).
    /// Исключение из показа наверх не уходит.
    /// </summary>
    /// <param name="show">Само действие показа (обычно <c>TrayIconHost.ShowNotification</c>).</param>
    /// <param name="log">Куда писать журнал панели — и успех, и причину отказа.</param>
    /// <param name="title">Заголовок сообщения: по нему в журнале видно, КАКОЕ уведомление не показано.</param>
    /// <returns><c>true</c> — показано; <c>false</c> — не показано, причина записана в журнал.</returns>
    internal static bool Announce(Action show, Action<string> log, string title)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            show();
        }
        catch (Exception error)
        {
            // Ловим Exception, а не два названных типа: дверь обязана держать ЛЮБОЕ исключение
            // показа, иначе следующее новое исключение снова уронит панель.
            Report(log, title, error);
            return false;
        }

        // ⚠️ СТРОКА «ПОКАЗАНО» ПИШЕТСЯ ТОЛЬКО ЗДЕСЬ — ПОСЛЕ успешного показа. До 2.2.1 она
        // писалась ДО вызова, и журнал утверждал «показано» даже тогда, когда показ падал
        // (в падении t17 через миллисекунды процесс умирал — разбор врал).
        ReportShown(log, title);
        return true;
    }

    /// <summary>Строка «показано». Журнал может отказать и здесь — дверь остаётся дверью.</summary>
    private static void ReportShown(Action<string> log, string title)
    {
        try
        {
            log(string.Format(CultureInfo.CurrentCulture, PanelStrings.NotifyShownLogFormat, title));
        }
        catch
        {
            // Журнал не принял и эту строку: панель важнее строки.
        }
    }

    /// <summary>
    /// Записать причину отказа. Журнал может отказать сам (диск, права, закрытый файл) — и тогда
    /// дверь обязана остаться дверью: исключение из журнала тоже не уходит вверх.
    /// </summary>
    private static void Report(Action<string> log, string title, Exception error)
    {
        try
        {
            log(string.Format(
                CultureInfo.CurrentCulture,
                PanelStrings.NotifyFailedLogFormat,
                title,
                error.GetType().Name,
                error.Message));
        }
        catch
        {
            // Журнал не принял строку. Панель важнее строки: молча — но процесс жив.
        }
    }
}


