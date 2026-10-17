using DshPanel.Tray;

namespace DshPanel.Shell;

/// <summary>
/// Меню значка в трее. Собирается ЧИСТОЙ функцией — и это не мелочь: состав меню и то,
/// что каждый пункт делает, проверяются тестами, не поднимая ни значка, ни окна, ни
/// рабочего стола владельца.
///
/// Состав: **четыре строки состояния** (сервер, агент с балансом, тариф, обновление),
/// разделитель, затем команды — показать панель, открыть агента в браузере, настройки,
/// о программе, проверить обновления, обновление панели — разделитель и выход. Порядок задан
/// владельцем 26.09.2026 (выход последним и отделён — случайно не нажмёшь), а строки состояния —
/// его же просьбой 27.09.2026: в v1 меню показывало состояние, и без него человек, не открывая
/// окно, не знает, работает ли DSH и какой агент отслеживается. Четвёртая строка (обновление)
/// встала в тот же ряд по тому же доводу: «проверять обновления» — это тоже состояние, которое
/// человек читает не открывая окна.
///
/// ⚠️ **Пункт «Обновление панели…» открывает РАЗДЕЛ «Обновление» настроек, а не отдельное окно**
/// (решение владельца 09.10.2026: *«Я не хочу отдельно вызываемое окно»*). Отдельного окна
/// обновления у панели больше нет вовсе, поэтому и двери в него здесь быть не может: пункт ведёт
/// туда же, куда «Настройки…», только сразу на нужный раздел.
///
/// ⚠️ Строки состояния — НЕ пункты: они серые и ничего не делают (<see cref="TrayMenuItem.Info"/>).
///
/// ⚠️ **Собирается на КАЖДЫЙ показ меню**, а не один раз при старте: состояние меняется, и меню,
/// собранное при запуске панели, врало бы про сервер весь день. Поэтому связка отдаёт его
/// провайдером (<c>TrayIconHost.SetMenuProvider</c>), а не готовым снимком.
/// </summary>
public static class PanelMenu
{
    /// <param name="status">
    /// Четыре строки состояния на момент показа. Функция, а не готовые строки: значение обязано
    /// быть СВЕЖИМ — меню открывается через час после старта панели, и состояние за это время
    /// меняется.
    /// </param>
    /// <param name="show">Показать окно панели.</param>
    /// <param name="openAgent">
    /// Открыть панель в браузере. Ссылка (в ней токен) наружу не выходит: пункт лишь просит
    /// связку сделать это — и она делает это ровно по щелчку человека, а в прогоне проверки
    /// не делает вовсе.
    /// </param>
    /// <param name="settings">Открыть окно настроек.</param>
    /// <param name="about">Открыть окно «О программе».</param>
    /// <param name="checkUpdate">
    /// СПРОСИТЬ ВЫПУСКИ СЕЙЧАС — мимо суточного гейта. Это тот же щелчок человека, что кнопка
    /// «Проверить сейчас» в разделе «Обновление» настроек (`UpdateController.Check`), и второго
    /// способа попросить проверку в панели быть не должно.
    /// </param>
    /// <param name="updateSection">
    /// Открыть РАЗДЕЛ «Обновление» окна настроек — дверь к тому, что нашлось. Отдельного окна
    /// обновления в панели нет (решение владельца 09.10.2026), поэтому и дверь ведёт в раздел.
    /// </param>
    /// <param name="exit">Завершить панель целиком.</param>
    public static TrayMenu Build(
        Func<TrayStatusLines> status,
        Action show,
        Action openAgent,
        Action settings,
        Action about,
        Action checkUpdate,
        Action updateSection,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(openAgent);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(about);
        ArgumentNullException.ThrowIfNull(checkUpdate);
        ArgumentNullException.ThrowIfNull(updateSection);
        ArgumentNullException.ThrowIfNull(exit);

        var lines = status() ?? throw new InvalidOperationException("строки состояния трея не собрались");

        var items = new List<TrayMenuItem>
        {
            // Шапка — состояние. Порядок: сервер (работает ли DSH вообще), агент (за кем следим
            // и сколько на нём денег), тариф (дорого ли сейчас), обновление (не пора ли обновиться).
            // Тон строки уезжает в меню вместе с текстом: цвет несёт тот же смысл, что слова
            // (решение владельца 27.09.2026).
            TrayMenuItem.Info(lines.Server.Text, lines.Server.Tone),
            TrayMenuItem.Info(lines.Agent.Text, lines.Agent.Tone),
            TrayMenuItem.Info(lines.Peak.Text, lines.Peak.Tone),
            TrayMenuItem.Info(lines.Update.Text, lines.Update.Tone),
        };

        // СТРОКА ТЕЛЕФОНА — ТОЛЬКО КОГДА ОНА ЕСТЬ (F1.d). Решение «есть ли она» принимает
        // не меню, а <see cref="Server.PhoneDecisions.TrayLineShown"/>: у пустой строки в меню
        // места нет вовсе, и это требование владельца, а не оформление.
        //
        // ⚠️ Порядок строк сохранён: телефон встаёт ПОСЛЕ обновления, а не перед ним, и пятым
        // по счёту, а не вторым. Иначе у человека, который привык, что вторая строка — про агента
        // с балансом, меню читалось бы заново.
        if (lines.HasPhone) items.Add(TrayMenuItem.Info(lines.Phone.Text, lines.Phone.Tone));

        items.Add(TrayMenuItem.Separator());

        items.AddRange(new[]
        {
            // Жирный по умолчанию: двойной щелчок по значку делает то же самое.
            TrayMenuItem.Command(PanelStrings.ShowPanelText, show, isDefault: true),

            // Подпись берётся у кнопки главного окна: дверь в панель одна и та же, и два разных
            // текста для неё разошлись бы.
            TrayMenuItem.Command(PanelStrings.OpenAgentButton, openAgent),
            TrayMenuItem.Command(PanelStrings.SettingsMenuText, settings),

            // Подпись берётся у кнопки главного окна: дверь в окно «О программе» одна и та же,
            // и два разных текста для неё разошлись бы (многоточие — как у настроек: за кнопкой
            // окно, а не действие).
            TrayMenuItem.Command(PanelStrings.AboutButton, about),

            // ОБНОВЛЕНИЯ — двумя пунктами, и они разные по смыслу: «Проверить обновления» делает
            // работу (идёт в сеть), «Обновление панели…» открывает НАСТРОЙКИ на разделе
            // «Обновление» — там видно, что нашлось, и оттуда же панель готовит замену.
            // Слить их в один значило бы либо не показать человеку результат проверки, либо
            // заставить его каждый раз ждать открытия окна ради ответа «новее ничего нет».
            TrayMenuItem.Command(PanelStrings.CheckUpdateMenuText, checkUpdate),
            TrayMenuItem.Command(PanelStrings.UpdateSectionMenuText, updateSection),

            TrayMenuItem.Separator(),
            TrayMenuItem.Command(PanelStrings.ExitText, exit),
        });

        return new TrayMenu(items);
    }
}
