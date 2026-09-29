using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>
/// Применение темы ИЗ ЛЮБОЙ НИТКИ — единственная дверь, которая об этом думает.
///
/// Тема — объект Avalonia (<c>Application.RequestedThemeVariant</c>), и трогать его можно только
/// на нитке интерфейса. Живой случай владельца 26.09.2026 в 23:10:12: человек нажал «Взять под
/// управление», встраивание шло в ФОНЕ, применение темы упало на «The calling thread cannot
/// access this object because a different thread owns it» — и панель записала в журнал, что
/// запомнить согласие НЕ УДАЛОСЬ, хотя файл настроек был уже записан и согласие действовало.
///
/// Здесь НЕТ ни одной ссылки на Avalonia: «где мы» и «как отправить» приходят делегатами.
/// Поэтому правило проверяется подставными делегатами — без окна, без ниток и без приложения.
/// Обвязка (в ней одной и живёт <c>Dispatcher</c>) — <c>App.axaml.cs</c>.
/// </summary>
public static class ThemeApply
{
    /// <summary>
    /// Применить тему: сейчас же, если мы на нитке интерфейса, иначе — отправить туда.
    ///
    /// Не бросает НИКОГДА: сбой уходит в <paramref name="failed"/>. Зовут это из сохранения
    /// настроек, а настройки к этому мгновению уже записаны — упасть здесь значит отчитаться
    /// о состоявшейся записи как о неудаче (ровно эта ложь и попала в журнал 26.09.2026).
    /// </summary>
    /// <param name="onUiThread">Мы на нитке интерфейса? Спрашивается один раз и здесь же.</param>
    /// <param name="applyNow">Как применить тему — там, где это разрешено.</param>
    /// <param name="post">Как отправить работу в нитку интерфейса.</param>
    /// <param name="theme">Что применять.</param>
    /// <param name="failed">Куда сказать о сбое: причину называет она, а не молчание.</param>
    public static void Apply(
        Func<bool> onUiThread,
        Action<PanelTheme> applyNow,
        Action<Action> post,
        PanelTheme theme,
        Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(onUiThread);
        ArgumentNullException.ThrowIfNull(applyNow);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(failed);

        if (onUiThread())
        {
            Attempt();
            return;
        }

        // Отправленное действие ловит свой сбой САМО: исключение в чужой нитке не поймает
        // уже никто, и панель упала бы вместе с ним.
        post(Attempt);

        void Attempt()
        {
            try
            {
                applyNow(theme);
            }
            catch (Exception ex)
            {
                failed(ex);
            }
        }
    }

    /// <summary>
    /// Строка журнала о сохранённых настройках, тему которых применить не удалось. Одна на всю
    /// панель: и сохранение из окна настроек, и отправка темы в нитку интерфейса говорят об одном
    /// и том же, и разойтись эти два текста не должны.
    /// </summary>
    public static string FailedLogLine(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return string.Format(
            CultureInfo.CurrentCulture,
            PanelStrings.SettingsThemeFailedLogFormat,
            error.GetType().Name,
            error.Message);
    }
}
