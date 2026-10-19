using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Server;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ПРАВИЛО БРАНДМАУЭРА В РАЗДЕЛЕ «ТЕЛЕФОН»: СЛОВА ДО, ОДНА СТРОКА ПОСЛЕ, КНОПКА (этап F4, кусок 1).
///
/// **Что здесь и чего здесь НЕТ.** Настоящий брандмауэр этой машины не трогается ни разу: дверь
/// подставная, у неё своё состояние (стоит правило или нет) и две беды, которые бывают у настоящей
/// (человек не подтвердил запрос Windows · машина просьбу пропустила, а правило не изменила).
/// Проверяется то, что видит и делает человек: состояние чтением, слова ДО нажатия, одна строка
/// ПОСЛЕ, отказ подтверждения НИЧЕГО не меняет, и без нажатия не происходит ничего вовсе.
///
/// Мутации, которые обязаны упасть здесь: «делает без щелчка» · «при отказе повышения считает,
/// что сделано» · «после нажатия не сказано ничего» · «кнопка нажимается, когда читать не удалось» ·
/// «прогону проверки дана настоящая дверь» · «состояние на экране не то, что прочитано».
/// </summary>
[Collection(PanelLookCollection.Name)]
public class FirewallSectionTests
{
    private static void Settle() => PanelTestStand.Settle();

    /// <summary>
    /// ДОЖДАТЬСЯ ФОНА, А НЕ СПАТЬ НАУГАД. Изменение идёт в фоновой нитке (иначе окно замёрзло бы
    /// на запросе Windows и слова ДО не успели бы появиться) — значит проверка обязана ЖДАТЬ ответ,
    /// прокручивая очередь интерфейса, а не «поспать и посмотреть».
    /// </summary>
    private static void WaitFor(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < until)
        {
            Settle();

            if (done()) return;

            Thread.Sleep(20);
        }

        Assert.Fail($"не дождались: {what}");
    }

    private static string Text(SettingsWindow window, string name) =>
        window.FindControl<TextBlock>(name)!.Text ?? string.Empty;

    /// <summary>Окно раздела «Телефон» с подставным брандмауэром (или без него — как в прогоне проверки).</summary>
    private static SettingsWindow Stand(string dir, IFirewallDoor? firewall) =>
        PanelTestStand.SettingsStand(dir, allowed: false, firewall: firewall);

    /// <summary>
    /// НАЖАТИЕ: СЛОВА ДО, ПОТОМ ДЕЛО, ПОТОМ ОДНА СТРОКА — и правило ПОЯВЛЯЕТСЯ.
    ///
    /// Мутации, которые обязаны упасть здесь: «слова ДО не сказаны» · «после нажатия не сказано,
    /// что сделано» · «состояние на экране не перечитано» · «правило поставлено не на тот порт».
    /// </summary>
    [AvaloniaFact]
    public void Нажатие_говорит_до_и_после_и_ставит_правило()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var machine = new FakeFirewall();

            var window = Stand(dir, machine);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                // ПРАВИЛА НЕТ — состояние сказано словами, а СЛОВА ДО видны ЕЩЁ ДО нажатия.
                Assert.Equal(PanelStrings.FirewallStateAbsent, Text(window, "FirewallStateText"));

                var before = window.FindControl<TextBlock>("FirewallBeforeText")!;
                var button = window.FindControl<Button>("FirewallButton")!;

                Assert.True(before.IsVisible, "слова ДО не показаны");
                Assert.Equal(FirewallDecisions.BeforeAdd(43127), before.Text);
                Assert.True(button.IsVisible);
                Assert.Equal(PanelStrings.FirewallAllowButton, PanelTestStand.Label(button));
                Assert.True(button.IsEnabled);

                // И ДО НАЖАТИЯ НЕ ПРОИЗОШЛО НИЧЕГО: машина не тронута.
                Assert.Equal(0, machine.Adds);
                Assert.False(machine.Rule);

                // НАЖИМАЕМ — и ждём фоновый ответ.
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                var result = window.FindControl<TextBlock>("FirewallResultText")!;

                WaitFor(() => result.Text == FirewallDecisions.DoneAdd(43127), "строку ПОСЛЕ");

                Assert.True(result.IsVisible);
                Assert.Equal(1, machine.Adds);
                Assert.True(machine.Rule);
                Assert.Equal(43127, machine.LastPort);

                // И СОСТОЯНИЕ ПЕРЕЧИТАНО: теперь предлагается убрать.
                Assert.Equal(PanelStrings.FirewallRemoveButton, PanelTestStand.Label(button));
                Assert.Equal(PanelStrings.FirewallBeforeRemove, before.Text);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ОТКАЗ ПОДТВЕРЖДЕНИЯ WINDOWS: НИЧЕГО НЕ ИЗМЕНЕНО, И ЭТО СКАЗАНО СЛОВАМИ.
    ///
    /// Требование владельца дословно: «отказ человека в повышении обязан быть сказан словами,
    /// а правило при отказе — не появляться».
    ///
    /// Мутации, которые обязаны упасть здесь: «при отказе повышения считает, что сделано» ·
    /// «при отказе правило всё равно появляется» · «отказ показан как поломка панели».
    /// </summary>
    [AvaloniaFact]
    public void Отказ_подтверждения_ничего_не_меняет()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var machine = new FakeFirewall { Refuse = true };

            var window = Stand(dir, machine);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                var button = window.FindControl<Button>("FirewallButton")!;

                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                var result = window.FindControl<TextBlock>("FirewallResultText")!;

                WaitFor(() => result.Text == FirewallDecisions.Refused(), "слова об отказе");

                Assert.Equal(FirewallDecisions.Refused(), result.Text);
                Assert.False(machine.Rule, "при отказе подтверждения правило появилось");
                Assert.Equal(PanelStrings.FirewallStateAbsent, Text(window, "FirewallStateText"));

                // Второй раз кнопка снова живая: отказ — не поломка, человек вправе попробовать ещё.
                Assert.True(button.IsEnabled);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПРАВИЛО ЕСТЬ — КНОПКА УБИРАЕТ, И УБИРАЕТ ИМЕННО ЕГО.
    ///
    /// Мутации, которые обязаны упасть здесь: «снятие не позвано вовсе» · «после снятия состояние
    /// не перечитано» · «слова ДО сказаны не про снятие».
    /// </summary>
    [AvaloniaFact]
    public void Правило_есть_кнопка_убирает()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var machine = new FakeFirewall { Rule = true };

            var window = Stand(dir, machine);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                var button = window.FindControl<Button>("FirewallButton")!;

                Assert.Equal(PanelStrings.FirewallRemoveButton, PanelTestStand.Label(button));
                Assert.Equal(FirewallDecisions.BeforeRemove(), Text(window, "FirewallBeforeText"));

                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                var result = window.FindControl<TextBlock>("FirewallResultText")!;

                WaitFor(() => result.Text == FirewallDecisions.DoneRemove(), "слова о снятии");

                Assert.Equal(1, machine.Removes);
                Assert.False(machine.Rule);
                Assert.Equal(PanelStrings.FirewallStateAbsent, Text(window, "FirewallStateText"));
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// НЕ ПРОЧИТАЛОСЬ — КНОПКА НЕ НАЖИМАЕТСЯ. «Не прочиталось» и «правила нет» — разные вещи:
    /// поставить правило поверх неизвестного значило бы однажды получить два с одним именем.
    ///
    /// Мутации, которые обязаны упасть здесь: «состояние читается неправдой (Unknown выдан за
    /// Absent)» · «кнопка разрешает действие, когда состояние неизвестно».
    /// </summary>
    [AvaloniaFact]
    public void Состояние_неизвестно_кнопка_не_нажимается()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, new BlindFirewall());
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                var button = window.FindControl<Button>("FirewallButton")!;

                Assert.False(button.IsEnabled, "кнопка жива, хотя состояние неизвестно");

                var state = Text(window, "FirewallStateText");

                Assert.NotEqual(PanelStrings.FirewallStateAbsent, state);
                Assert.NotEqual(FirewallDecisions.Ready(43127).Words, state);

                // И СЛОВ ДО НЕТ: обещать изменение, которое не помещается в известное состояние,
                // панель не имеет права.
                Assert.False(window.FindControl<TextBlock>("FirewallBeforeText")!.IsVisible);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ДВЕРИ НЕТ — РАЗДЕЛ ГОВОРИТ, ЧТО БРАНДМАУЭРОМ ЗДЕСЬ НЕ УПРАВЛЯЮТ, и НИ ОДНОГО правила
    /// не читает. Так его видит прогон проверки (и кадр `settings-phone`).
    ///
    /// Мутации, которые обязаны упасть здесь: «прогону проверки дана дверь» · «пустая кнопка вместо
    /// слов» · «раздел молчит о том, что не управляет».
    /// </summary>
    [AvaloniaFact]
    public void Без_двери_раздел_говорит_словами()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var window = Stand(dir, firewall: null);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                Assert.Equal(PanelStrings.FirewallNoDoor, Text(window, "FirewallStateText"));
                Assert.False(window.FindControl<Button>("FirewallButton")!.IsVisible);
                Assert.False(window.FindControl<TextBlock>("FirewallBeforeText")!.IsVisible);

                // И подписи карточки на месте: пустой карточки без объяснения быть не должно.
                Assert.Equal(PanelStrings.FirewallTitle, Text(window, "FirewallTitleText"));
                Assert.Equal(PanelStrings.FirewallNote, Text(window, "FirewallNoteText"));
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ОДНО НАЖАТИЕ — ОДНО ИЗМЕНЕНИЕ. Пока идёт запрос подтверждения Windows, второе нажатие
    /// ничего не добавляет: иначе панель поставила бы два правила с одним именем.
    ///
    /// ⚠️ Нажимаем ДВЕРЬЮ НАЖАТИЯ (<c>PressFirewall</c>), а не кнопкой: кнопка на время изменения
    /// выключается и события не даёт вовсе — через неё предохранитель «одно нажатие — одно изменение»
    /// проверен быть не может, а он нужен на случай второго запроса (не от кнопки).
    /// </summary>
    [AvaloniaFact]
    public void Второе_нажатие_пока_идёт_первое_ничего_не_делает()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var machine = new FakeFirewall { Slow = true };

            var window = Stand(dir, machine);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                window.PressFirewall();
                window.PressFirewall();
                window.PressFirewall();

                var result = window.FindControl<TextBlock>("FirewallResultText")!;

                WaitFor(() => result.Text == FirewallDecisions.DoneAdd(43127), "строку ПОСЛЕ");

                Assert.Equal(1, machine.Adds);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// СЪЁМКА КАДРА `settings-phone` ДВЕРИ НЕ ДАЁТ — проверяется НА ЖИВОМ ПУТИ СЪЁМКИ
    /// (тем же членом, каким кадр собирается), а не на своей сборке окна.
    ///
    /// Мутации, которые обязаны упасть здесь: «в прогоне проверки раздел читает правила машины» ·
    /// «прогону проверки дана настоящая применяющая дверь».
    /// </summary>
    [AvaloniaFact]
    public void Кадр_съёмки_двери_не_имеет()
    {
        var dir = PanelTestStand.TempDir();
        var paths = AppPaths.Under(dir);

        try
        {
            var server = new ServerController(
                paths.DshHome,
                () => paths.DataDir,
                _ => { },
                mayOccupyOwnerPort: false,
                probe: ServerProbe.System(),
                ownEngineDir: () => paths.EngineDir);

            var window = Program.BuildShotSettingsWindow(
                StartModes.ShotSettingsPhone,
                paths,
                server,
                autostart: null);

            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                Assert.Equal(PanelStrings.FirewallNoDoor, Text(window, "FirewallStateText"));
                Assert.False(window.FindControl<Button>("FirewallButton")!.IsVisible);
            }
            finally
            {
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }

    /// <summary>
    /// ПОДСТАВНОЙ БРАНДМАУЭР: состояние, две беды и счёт действий.
    ///
    /// ⚠️ <see cref="Slow"/> — «человек думает над запросом Windows»: дверь отвечает не сразу,
    /// и только так проверяется, что второе нажатие ничего не добавит.
    ///
    /// ⚠️ **Счёт — СРАЗУ при вызове и через <c>Interlocked</c>**, и это нашла мутация (AC8 сначала
    /// выжила): три одновременных вызова увеличивали счётчик ПОСЛЕ сна и теряли свои приращения —
    /// «три изменения» выглядели как одно, и предохранитель «одно нажатие — одно изменение»
    /// проверялся бы слепым счётом.
    /// </summary>
    private sealed class FakeFirewall : IFirewallDoor
    {
        private int _adds;
        private int _removes;

        public bool Rule { get; set; }

        public bool Refuse { get; set; }

        public bool Slow { get; set; }

        public int Adds => Volatile.Read(ref _adds);

        public int Removes => Volatile.Read(ref _removes);

        public int LastPort { get; private set; }

        public FirewallState Read(int port) =>
            Rule
                ? FirewallDecisions.Ready(port)
                : FirewallDecisions.Absent();

        public FirewallChange Add(int port)
        {
            LastPort = port;
            Interlocked.Increment(ref _adds);

            if (Slow) Thread.Sleep(150);

            if (Refuse) return FirewallChange.Refusal();

            Rule = true;

            return FirewallChange.Yes(string.Empty);
        }

        public FirewallChange Remove()
        {
            Interlocked.Increment(ref _removes);

            if (Slow) Thread.Sleep(150);

            if (Refuse) return FirewallChange.Refusal();

            Rule = false;

            return FirewallChange.Yes(string.Empty);
        }
    }

    /// <summary>Брандмауэр, который ничего не отвечает: состояние — «прочитать не удалось».</summary>
    private sealed class BlindFirewall : IFirewallDoor
    {
        public FirewallState Read(int port) =>
            new(FirewallPresence.Unknown, Loc.T(PanelStrings.FirewallStateUnknownFormat, "netsh: no output"));

        public FirewallChange Add(int port) => FirewallChange.No("нельзя");

        public FirewallChange Remove() => FirewallChange.No("нельзя");
    }

    /// <summary>
    /// ОКНО НАСТРОЕК БЕЗ НИТКИ ИНТЕРФЕЙСА — ГРОМКИЙ ОТКАЗ (t13), и отказ называет СВОЁ окно.
    /// Постройку проверить нельзя (базовый `Window` падает первым), поэтому проверяется решение,
    /// которое зовёт конструктор.
    /// </summary>
    [Fact]
    public void Окно_настроек_без_нитки_интерфейса_отказывается_строиться()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => SettingsWindow.RequireUiThread(null));

        Assert.Contains("ОКНО НАСТРОЕК БЕЗ НИТКИ ИНТЕРФЕЙСА", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Стройте окно на нитке интерфейса", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТВЕТ ФОНОВОЙ РАБОТЫ ДОХОДИТ ДО ОКНА И ПОСЛЕ ЗАБЫВАНИЯ ДВЕРИ (t13) — третий путь класса Н-1.
    ///
    /// Через `OnUi` снимается занятость: пожарная кнопка выключается ДО фона
    /// (`_firewallBusy = true; FirewallButton.IsEnabled = false`), и если ответ не дошёл,
    /// она осталась бы выключенной навсегда, а строка результата — «ждём подтверждения».
    /// Проверка детерминированная: окно построено (свою нитку знает) → дверь забыта → нажатие →
    /// ответ обязан дойти. На `UiThread.Post` (без правки) ответ теряется.
    /// </summary>
    [AvaloniaFact]
    public void Ответ_фоновой_работы_доходит_до_окна_и_после_забывания_двери()
    {
        var dir = PanelTestStand.TempDir();

        try
        {
            var machine = new FakeFirewall { Slow = true };

            var window = Stand(dir, machine);
            window.ShowSection(SettingsWindow.PhoneSection);
            window.Show();
            Settle();

            try
            {
                // Чужая проверка двери очищает память нитки интерфейса — ровно это и происходит
                // в полном прогоне, когда UiThreadTests идёт параллельно с окнами.
                UiThread.ForgetForTests();

                var button = window.FindControl<Button>("FirewallButton")!;
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                var result = window.FindControl<TextBlock>("FirewallResultText")!;

                WaitFor(() => result.Text == FirewallDecisions.DoneAdd(43127), "строку ПОСЛЕ");

                // Занятость снята: кнопка снова доступна (состояние перечитано — правило стоит).
                Assert.True(button.IsEnabled, "кнопка брандмауэра осталась выключенной после ответа");
                Assert.Equal(1, machine.Adds);
            }
            finally
            {
                UiThread.Remember();
                window.CloseQuietly();
            }
        }
        finally
        {
            PanelTestStand.RemoveTemp(dir);
        }
    }
}