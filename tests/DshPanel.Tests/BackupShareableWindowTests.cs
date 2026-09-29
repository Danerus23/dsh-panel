using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DshPanel.Backup;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ГАЛОЧКА «ЭТА КОПИЯ — ДЛЯ ПЕРЕДАЧИ» В ОКНЕ КОПИЙ: разовое действие, а не настройка.
///
/// Решения владельца, и оба проверяются здесь ЧЕРЕЗ НАСТОЯЩЕЕ ОКНО:
///
/// * **п. 11 (27.09.2026):** передача становится разовым действием на ОДНУ копию — его слова:
///   *«функция по созданию копии и передачи кому-либо смешалась с настройками резервных копий,
///   хотя это просто отдельная фича»*. Значит галочка обязана **сняться сама после копии**:
///   иначе вторая копия молча стала бы передаваемой;
/// * **п. 12 (29.09.2026):** сочетание «приватные ключи» + «копия для передачи» **запрещено**
///   и объяснено словами. Причина: вместе они дают архив с приватными ключами человека и без ключа
///   модели — ровно тот, который нельзя передавать, а он назван «для передачи».
///
/// ⚠️ Окно здесь НАСТОЯЩЕЕ, а домен под ним — подставной (<c>StubBackups</c>): он запоминает,
/// с каким решением о передаче его позвали. Настоящий домен снял бы настоящий архив, и проверка
/// «галочка снялась» превратилась бы в проверку копии — а её место в <c>BackupShareableTests</c>.
/// </summary>
[Collection(PanelLookCollection.Name)]
public class BackupShareableWindowTests
{
    private static (BackupWindow Window, BackupWindowStopFlowTests.StubBackups Backups) Stand()
    {
        var backups = new BackupWindowStopFlowTests.StubBackups();
        var window = new BackupWindow();

        window.Attach(backups);
        window.Show();
        PanelTestStand.Settle();

        return (window, backups);
    }

    /// <summary>Нажать настоящую кнопку окна — тот же путь, каким идёт человек.</summary>
    private static void Нажать(BackupWindow window, string buttonName) =>
        window.FindControl<Button>(buttonName)!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Дождаться условия: копия идёт в фоне и заканчивается в чужой нити.</summary>
    private static bool WaitFor(Func<bool> condition, int milliseconds = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (DateTime.UtcNow < deadline && !condition())
        {
            System.Threading.Thread.Sleep(50);
            PanelTestStand.Settle();
        }

        return condition();
    }

    /// <summary>
    /// ГАЛОЧКА СНИМАЕТСЯ САМА ПОСЛЕ КОПИИ, а копия уходит с решением «для передачи».
    ///
    /// Проверяются ОБЕ половины, и порознь: «снялась» без «доехала» одинаково верно для галочки,
    /// которая вообще ничего не значит, — а именно это и было бедой, названной владельцем.
    /// </summary>
    [AvaloniaFact]
    public void Галочка_доезжает_до_копии_и_снимается_сама()
    {
        var (window, backups) = Stand();

        try
        {
            var check = window.FindControl<CheckBox>("ShareableCheck");
            var warning = window.FindControl<TextBlock>("ShareableWarningText");

            Assert.NotNull(check);
            Assert.NotNull(warning);

            // До щелчка: галочка снята (это НЕ настройка — берётся из состояния окна),
            // и предупреждения о передаче нет.
            Assert.False(check!.IsChecked);
            Assert.False(warning!.IsVisible);

            check.IsChecked = true;
            PanelTestStand.Settle();

            Assert.True(warning.IsVisible, "предупреждение о передаче не показано при стоящей галочке");

            Нажать(window, "CreateButton");

            Assert.True(WaitFor(() => backups.CreateCalls == 1), "копия не началась");
            Assert.True(backups.LastShareable == true, "решение «для передачи» не доехало до копии");

            // ⚠️ ГЛАВНОЕ: галочка снята САМА, и окно об этом молчит словами передачи.
            Assert.False(check.IsChecked, "галочка осталась стоять после копии — вторая копия стала бы передаваемой");
            Assert.False(warning.IsVisible, "предупреждение о передаче осталось после копии");
            Assert.False(
                window.FindControl<TextBlock>("ShareableNoteText")!.Text == string.Empty,
                "пояснение о СВОЕЙ копии не вернулось после снятия галочки");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// СОЧЕТАНИЕ С ПРИВАТНЫМИ КЛЮЧАМИ НЕВОЗМОЖНО, и причина названа словами ТАМ ЖЕ, где человек
    /// ставит галочку: молча недоступная галочка читалась бы как «панель сломалась». Она именно
    /// НЕДОСТУПНА — запрещённое сочетание нельзя даже выбрать, а не «выбрать и получить отказ после».
    ///
    /// ⚠️ И копия при этом снимается СВОЯ, с ключами: запрет про сочетание, а не про копирование.
    /// Отобрать у человека обычную копию из-за выключенной галочки было бы наказанием не за то.
    /// </summary>
    [AvaloniaFact]
    public void При_приватных_ключах_галочку_поставить_нельзя_и_сказано_почему()
    {
        var (window, backups) = Stand();

        try
        {
            backups.WithKeys = true;
            window.Render();
            PanelTestStand.Settle();

            var check = window.FindControl<CheckBox>("ShareableCheck");
            var note = window.FindControl<TextBlock>("ShareableNoteText");

            Assert.NotNull(check);
            Assert.NotNull(note);

            // Причина видна ДО всякого щелчка: сочетание запрещено, и об этом сказано в окне.
            Assert.Equal(PanelStrings.BackupShareableWithKeysRefused, note!.Text);
            Assert.False(check!.IsEnabled, "галочку запрещённого сочетания можно поставить");
            Assert.False(check.IsChecked);

            // Даже если щелчок случится (каркас, горячая клавиша, будущая правка) — сочетание
            // не остаётся выбранным, и причина встаёт в строку состояния.
            check.IsChecked = true;
            PanelTestStand.Settle();

            Assert.False(check.IsChecked, "запрещённое сочетание осталось выбранным в окне");
            Assert.Equal(PanelStrings.BackupShareableWithKeysRefused, window.Status);

            // Копия снимается СВОЯ — и это не отказ: запрет про сочетание, а не про копирование.
            Нажать(window, "CreateButton");
            Assert.True(WaitFor(() => backups.CreateCalls == 1), "обычная копия не началась");
            Assert.False(backups.LastShareable, "копия ушла передаваемой вопреки запрету");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// КЛЮЧИ ВКЛЮЧИЛИ ПРИ УЖЕ СТОЯЩЕЙ ГАЛОЧКЕ — копия ОТВЕРГАЕТСЯ СЛОВАМИ, а не снимается молча.
    ///
    /// Случай настоящий: разрешение на ключи живёт в настройках, окно настроек пишет его тем же
    /// файлом, а окно копий при этом открыто. Человек просил передаваемую копию — и получить
    /// ВМЕСТО неё обычную с ключами значило бы отдать ему не то, что он просил, и промолчать.
    /// Поэтому отказ, и он называет причину.
    /// </summary>
    [AvaloniaFact]
    public void Ключи_включили_при_стоящей_галочке_копия_отвергается_словами()
    {
        var (window, backups) = Stand();

        try
        {
            var check = window.FindControl<CheckBox>("ShareableCheck");
            Assert.NotNull(check);

            check!.IsChecked = true;
            PanelTestStand.Settle();

            // Разрешение на ключи включили в настройках, пока это окно было открыто. Перерисовки
            // здесь НЕТ намеренно: проверяется тот самый щелчок «Создать», который человек делает
            // до того, как окно успело перечитать настройки.
            backups.WithKeys = true;

            Нажать(window, "CreateButton");
            PanelTestStand.Settle();

            Assert.False(WaitFor(() => backups.CreateCalls > 0, 1000), "копия началась вопреки запрету");
            Assert.Equal(PanelStrings.BackupShareableWithKeysRefused, window.Status);

            // И следующая отрисовка приводит окно в себя: галочка снята, причина названа.
            window.Render();
            PanelTestStand.Settle();

            Assert.False(check.IsChecked);
            Assert.False(check.IsEnabled);
            Assert.Equal(
                PanelStrings.BackupShareableWithKeysRefused,
                window.FindControl<TextBlock>("ShareableNoteText")!.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
