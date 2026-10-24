using System;
using System.Collections.Generic;
using System.Linq;
using DshPanel.Isolation;
using DshPanel.Shell;
using DshPanel.Tray;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЩЕЛЧОК ПО ШАРИКУ — ДВЕРЬ, А НЕ КАРТИНКА (u2, DEF-046).
///
/// До 2.4.0 событие `TrayIconHost.NotificationClicked` объявлялось и поднималось
/// (`TrayIconHost.cs:112, 597`), но **подписчиков у него было ноль**: человек щёлкал по
/// уведомлению — и не происходило ничего. Банк (`60-states.md` §5а) говорит прямо: «клик по
/// шарику — дверь или ничего», и шарик не должен обещать действие, которого нет.
///
/// **Что здесь доказывается, и почему это может упасть.**
///
/// 1. **Подписка есть.** У живого <see cref="TrayIconHost"/> после создания двери ровно один
///    подписчик. Мутация «снять подписку» (убрать `NotificationClicked += Click`) роняет это
///    утверждение и следующее — до 2.4.0 так и было: ноль подписчиков.
/// 2. **Щелчок ведёт в цель.** Щелчок поднимается ТЕМ ЖЕ путём, каким его поднимает оболочка
///    (`NIN_BALLOONUSERCLICK`), и дверь открывает то окно, о котором было сообщение.
/// 3. **Отказ уважается.** Если окно открыть не удалось — это не молчание: строка в журнал.
/// 4. **Сообщение не показано — клика нет.** Цель помнится только у показанного шарика: иначе
///    щелчок открывал бы окно «ни с того ни с сего» (и мешал бы в прогоне проверки).
/// 5. **Одно событие — одна цель.** Помнится последнее сообщение, а не все подряд.
///
/// ⚠️ **Чего кадром не докажешь:** сам шарик Windows рисует по-своему, и поймать его кадром
/// в панели уже записано как невозможное (самотест трея). Поэтому поведение доказывается
/// прогоном, а кадр показывает поверхность (шарик самотеста) и меню значка.
/// </summary>
public class NoticeClickTests
{
    /// <summary>Журнал в памяти: строки, которые дверь написала бы в журнал панели.</summary>
    private sealed class Log
    {
        public List<string> Lines { get; } = new();

        public void Write(string line) => Lines.Add(line);
    }

    // ------------------------------------------------------------------ карта видов

    /// <summary>
    /// КАРТА ЦЕЛЕЙ — ЧИСТАЯ ФУНКЦИЯ, И ОНА НАРОЧНО УЗКАЯ (решение капитана 24.10.2026, u2):
    /// только виды обновления (три) и сломанный автозапуск — в настройки. Остальные виды щелчок
    /// не открывает: у них свои задачи (сервер «упал сам» — u10, копии/пики/цены — свои окна),
    /// и второе место, решающее «куда ведёт сообщение», разошлось бы с первым.
    ///
    /// Перебираются ВСЕ значения перечисления: новый вид, забытый в карте, получит `None` и
    /// попадёт в счёт — проверка это заметит, а не промолчит.
    /// </summary>
    [Fact]
    public void Карта_ведёт_только_виды_u2_и_остальные_оставляет_без_двери()
    {
        var kinds = Enum.GetValues<NoticeKind>();

        Assert.Equal(14, kinds.Length); // 12 прежних + «идёт скачивание» + «сервер завершился сам» (2.4.0)

        Assert.Equal(3, kinds.Count(kind => NoticeTargets.Of(kind) == NoticeTarget.UpdateSection));
        Assert.Equal(1, kinds.Count(kind => NoticeTargets.Of(kind) == NoticeTarget.Settings));
        Assert.Equal(10, kinds.Count(kind => NoticeTargets.Of(kind) == NoticeTarget.None));

        // Поимённо: цели обновления и автозапуска — те, что решил капитан.
        Assert.Equal(NoticeTarget.UpdateSection, NoticeTargets.Of(NoticeKind.UpdatePreparing));
        Assert.Equal(NoticeTarget.UpdateSection, NoticeTargets.Of(NoticeKind.UpdateAvailable));
        Assert.Equal(NoticeTarget.UpdateSection, NoticeTargets.Of(NoticeKind.UpdateOutcome));
        Assert.Equal(NoticeTarget.Settings, NoticeTargets.Of(NoticeKind.AutostartBroken));

        // «Сервер упал сам» — задача u10: карта u2 его не трогает.
        Assert.Equal(NoticeTarget.None, NoticeTargets.Of(NoticeKind.ServerStop));
        Assert.Equal(NoticeTarget.None, NoticeTargets.Of(NoticeKind.ServerCrashed));

        // Слова цели для журнала: у каждой двери своё имя, и «none» — не имя двери.
        Assert.Equal("update", NoticeTargets.Name(NoticeTarget.UpdateSection));
        Assert.Equal("settings", NoticeTargets.Name(NoticeTarget.Settings));
        Assert.Equal("none", NoticeTargets.Name(NoticeTarget.None));
    }

    // ------------------------------------------------------------------ подписка и щелчок

    /// <summary>
    /// ПОДПИСЧИК ЕСТЬ, И ЩЕЛЧОК ВЕДЁТ В ЦЕЛЬ. Живой значок строится без показа (окно трея
    /// создаётся только в <c>Show</c>), щелчок поднимается тем же путём, что от оболочки.
    ///
    /// **Мутация «снять подписку»** (убрать строку `_tray.NotificationClicked += Click;`) роняет
    /// оба утверждения: подписчиков станет 0, цель не откроется. Именно это состояние и было
    /// в 2.3.2 — событие есть, подписчиков ноль.
    /// </summary>
    [Fact]
    public void Подписчик_есть_и_щелчок_ведёт_в_цель()
    {
        using var tray = new TrayIconHost("DSH Panel — прогон u2");

        var opened = new List<NoticeTarget>();
        var log = new Log();

        using var door = new NoticeClickDoor(
            tray,
            target =>
            {
                opened.Add(target);
                return true;
            },
            log.Write);

        Assert.Equal(1, tray.NotificationClickSubscribers);
        Assert.Equal(NoticeTarget.None, door.Target);

        // Шарик «идёт скачивание» показан — щелчок по нему ведёт в раздел «Обновление».
        door.Remember(NoticeKind.UpdatePreparing);
        Assert.Equal(NoticeTarget.UpdateSection, door.Target);

        tray.PostNotificationClickForSelfTest();

        Assert.Equal(new[] { NoticeTarget.UpdateSection }, opened);
        Assert.Single(log.Lines);
        Assert.Contains("открыт update", log.Lines[0], StringComparison.Ordinal);

        // Второй щелчок того же шарика — снова та же дверь: цель не «одноразовая».
        tray.PostNotificationClickForSelfTest();
        Assert.Equal(2, opened.Count);
    }

    /// <summary>
    /// ОТКАЗ УВАЖАЕТСЯ: окно не открылось — это не тишина. Дверь отвечает «не открыли» и пишет
    /// строку в журнал: по ней видно, что человек щёлкал, а окно не появилось.
    /// </summary>
    [Fact]
    public void Отказ_открыть_называется_строкой_в_журнале()
    {
        using var tray = new TrayIconHost("DSH Panel — прогон u2");

        var opened = 0;
        var log = new Log();

        using var door = new NoticeClickDoor(
            tray,
            _ =>
            {
                opened++;
                return false; // окно не открылось (связки нет, слот занят)
            },
            log.Write);

        door.Remember(NoticeKind.AutostartBroken);
        tray.PostNotificationClickForSelfTest();

        Assert.Equal(1, opened);
        Assert.Single(log.Lines);
        Assert.Contains("открыть нечем — settings", log.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// СООБЩЕНИЕ НЕ ПОКАЗАНО — КЛИКА НЕТ, И СТРОКИ НЕТ. Поправка дизайн-лидера 24.10.2026:
    /// причину отказа показа уже назвала дверь показа (`NotifyFailedLogFormat`), второй такой
    /// строки не надо. Главное — окно не открывается «ни с того ни с сего».
    /// </summary>
    [Fact]
    public void Не_показанное_сообщение_не_открывает_ничего()
    {
        using var tray = new TrayIconHost("DSH Panel — прогон u2");

        var opened = new List<NoticeTarget>();
        var log = new Log();

        using var door = new NoticeClickDoor(tray, target => { opened.Add(target); return true; }, log.Write);

        door.Forget();
        tray.PostNotificationClickForSelfTest();

        Assert.Empty(opened);
        Assert.Empty(log.Lines);
    }

    /// <summary>
    /// ОДНО СООБЩЕНИЕ — ОДНА ЦЕЛЬ: помнится последнее показанное. Два шарика подряд не должны
    /// открывать два окна по одному щелчку (банк §5а: одно событие — одно сообщение).
    /// </summary>
    [Fact]
    public void Помнится_последнее_сообщение_а_не_все_подряд()
    {
        using var tray = new TrayIconHost("DSH Panel — прогон u2");

        var opened = new List<NoticeTarget>();

        using var door = new NoticeClickDoor(tray, target => { opened.Add(target); return true; }, _ => { });

        door.Remember(NoticeKind.AutostartBroken);
        door.Remember(NoticeKind.UpdateOutcome);

        tray.PostNotificationClickForSelfTest();

        Assert.Equal(new[] { NoticeTarget.UpdateSection }, opened);
    }

    /// <summary>
    /// ВИД БЕЗ ДВЕРИ НЕ ОТКРЫВАЕТ ОКНО И НАЗЫВАЕТСЯ В ЖУРНАЛЕ. Обещания действия нет — это
    /// правило банка, а не удобство; строка в журнале нужна, чтобы щелчок не пропал бесследно.
    /// </summary>
    [Fact]
    public void Вид_без_двери_пишет_строку_и_ничего_не_открывает()
    {
        using var tray = new TrayIconHost("DSH Panel — прогон u2");

        var opened = new List<NoticeTarget>();
        var log = new Log();

        using var door = new NoticeClickDoor(tray, target => { opened.Add(target); return true; }, log.Write);

        door.Remember(NoticeKind.BackupFailed); // копии — своя задача, дверь u2 сюда не ведёт
        tray.PostNotificationClickForSelfTest();

        Assert.Empty(opened);
        Assert.Single(log.Lines);
        Assert.Contains("нет окна", log.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// УБОРКА СНИМАЕТ ПОДПИСКУ: у двери, которой больше нет, не должно оставаться щелчков.
    /// </summary>
    [Fact]
    public void Уборка_снимает_подписку()
    {
        using var tray = new TrayIconHost("DSH Panel — прогон u2");

        var opened = new List<NoticeTarget>();
        var door = new NoticeClickDoor(tray, target => { opened.Add(target); return true; }, _ => { });

        door.Remember(NoticeKind.UpdatePreparing);
        door.Dispose();

        Assert.Equal(0, tray.NotificationClickSubscribers);

        tray.PostNotificationClickForSelfTest();
        Assert.Empty(opened);
    }
}
