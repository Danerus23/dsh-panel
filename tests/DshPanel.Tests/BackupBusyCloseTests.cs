using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using DshPanel.Backup;
using DshPanel.Restore;
using DshPanel.Shell;
using DshPanel.Views;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЗАКРЫТИЕ ОКНА КОПИЙ НА СЕРЕДИНЕ КОПИИ (DEF-045, 2.4.0): СПРАШИВАЕТ, А НЕ МОЛЧИТ.
///
/// **От какого случая.** Окно закрывалось молча, даже когда копия шла: работа продолжалась
/// в фоне, а человек не знал ни что она идёт, ни где увидеть итог. Теперь закрытие отменяется,
/// задаётся вопрос («Закрыть окно, пока идёт копия?»), и ответ:
/// * «Остаться в окне» (безопасный, он же Esc) — окно не закрывается;
/// * «Закрыть, копия продолжится» — окно закрывается, копия идёт дальше, а ответ из фона уже
///   не трогает органы закрытого окна.
///
/// ⚠️ Диалог в прогоне не показать: ответ подставляется <see cref="BackupWindow.BusyCloseForTests"/>,
/// и это тот же приём, каким проверяется вопрос про сервер (`StopChoiceForTests`).
///
/// **Мутации, которые обязаны упасть:** убрать `Closing += OnClosing` (окно закрывается молча) ·
/// не отменять закрытие (вопрос не успевает задаться) · закрывать при любом ответе (безопасный
/// ответ перестаёт быть безопасным) · убрать проверку `_closed` в ответе из фона (закрытое окно
/// снова начинает писать в свою строку состояния).
/// </summary>
public class BackupBusyCloseTests
{
    /// <summary>
    /// Подставной домен копий: `CreateCopy` ЖДЁТ, пока проверка не отпустит, — только так окно
    /// можно застать «в середине копии», не снимая настоящей копии и не трогая чужих данных.
    /// </summary>
    private sealed class WaitingBackups : IBackupControl
    {
        public readonly ManualResetEventSlim Started = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim Finished = new(false);
        public int CreateCalls { get; private set; }

#pragma warning disable CS0067 // заглушка домена: событие объявлено требованием IBackupControl и в этой проверке не поднимается — окну копий здесь оно не нужно
        public event Action? Changed;
#pragma warning restore CS0067
        public string Folder => System.IO.Path.GetTempPath();
        public bool FolderFromDefault => true;
        public bool Writable => true;
        public IReadOnlyList<BackupEntry> Entries => Array.Empty<BackupEntry>();
        public string ListProblem => string.Empty;
        public string InstalledEngineVersion => "0.0.0-test";
        public bool WithKeys => false;
        public bool CanChangeComposition => true;
        public BackupScope Scope => BackupScope.Auto;
        public IReadOnlyList<string> ExtraExclusions => Array.Empty<string>();
        public bool SetScope(BackupScope scope) => true;
        public bool SetExtraExclusions(IReadOnlyList<string> names) => true;
        public BackupEstimate Estimate(BackupScope scope) => new(false, 0);
        public bool Busy => false;
        public IReadOnlyList<string> KeyDirectories => Array.Empty<string>();
        public IReadOnlyList<KeyDirSuggestion> KeySuggestions => Array.Empty<KeyDirSuggestion>();
        public bool SetWithKeys(bool value) => true;
        public bool AddKeyDirectory(string path) => false;
        public void Refresh()
        {
        }

        public string NextArchivePath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copy.zip");

        public CopyOutcome CreateCopy(string archivePath, bool serverRunning, bool shareable, Action<string>? progress = null)
        {
            CreateCalls++;
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            Finished.Set();

            return new CopyOutcome(true, string.Empty, null);
        }

        public RestorePlan PlanRestore(string archivePath, bool withEngine, bool withPanel, bool withKeys) =>
            new(false, "не нужно проверке", archivePath, null, string.Empty, Array.Empty<RestoreGroup>(), Array.Empty<string>(), 0);

        public RestoreOutcome RunRestore(
            RestorePlan plan, bool withEngine, bool withPanel, bool withKeys, bool serverRunning,
            Action<string>? progress = null) => new(true, string.Empty, null);
    }

    /// <summary>Начать копию так, как её начинает человек: щелчком по кнопке.</summary>
    private static void StartCopy(BackupWindow window)
    {
        var button = window.FindControl<Button>("CreateButton");
        Assert.NotNull(button);
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// СЕРЕДИНА КОПИИ + ЗАКРЫТИЕ = ВОПРОС, И БЕЗОПАСНЫЙ ОТВЕТ ОСТАВЛЯЕТ ОКНО.
    ///
    /// **Мутации, которые обязаны упасть:** убрать `Closing` (спросить не успели) · закрыть при
    /// ответе «остаться» (окно исчезает, хотя человек сказал обратное).
    /// </summary>
    [AvaloniaFact]
    public void Закрытие_во_время_копии_спрашивает_и_безопасный_ответ_оставляет_окно()
    {
        var backups = new WaitingBackups();
        var window = new BackupWindow();
        window.Attach(backups);

        try
        {
            var asked = 0;
            window.BusyCloseForTests = () =>
            {
                asked++;
                return BusyCloseChoice.Stay;
            };

            window.Show();
            PanelTestStand.Settle();

            StartCopy(window);
            Assert.True(backups.Started.Wait(TimeSpan.FromSeconds(5)), "копия не началась");
            Assert.True(window.Busy, "окно не считает себя занятым — проверка не через то состояние");

            window.Close();
            PanelTestStand.Settle();

            // СПРОСИЛИ, причём один раз, и окно осталось: это и есть безопасный ответ.
            Assert.Equal(1, asked);
            Assert.True(window.IsVisible, "окно закрылось, хотя человек выбрал «остаться»");
        }
        finally
        {
            backups.Release.Set();
            PanelTestStand.Settle();
            window.Close();
            backups.Release.Dispose();
            backups.Started.Dispose();
            backups.Finished.Dispose();
        }
    }

    /// <summary>
    /// ОТВЕТ «ЗАКРЫТЬ, КОПИЯ ПРОДОЛЖИТСЯ» ЗАКРЫВАЕТ ОКНО, А ОТВЕТ ИЗ ФОНА НЕ ТРОГАЕТ ЕГО ОРГАНЫ.
    ///
    /// Это вторая половина DEF-045: прежде ответ из фона писал в строку состояния уже закрытого
    /// окна. Здесь копия отпускается ПОСЛЕ закрытия — и строка остаётся той, что была («идёт
    /// копия»), а не превращается в итог.
    ///
    /// **Мутация, которая обязана упасть:** убрать `if (_closed) return;` в `RunInBackground`.
    /// </summary>
    [AvaloniaFact]
    public void Закрыть_копия_продолжится_закрывает_окно_и_фон_не_трогает_органы()
    {
        var backups = new WaitingBackups();
        var window = new BackupWindow();
        window.Attach(backups);

        try
        {
            window.BusyCloseForTests = () => BusyCloseChoice.LeaveInBackground;

            window.Show();
            PanelTestStand.Settle();

            StartCopy(window);
            Assert.True(backups.Started.Wait(TimeSpan.FromSeconds(5)), "копия не началась");

            var whileBusy = window.Status;

            window.Close();
            PanelTestStand.Settle();

            Assert.False(window.IsVisible, "окно не закрылось, хотя человек выбрал «закрыть»");

            // Копия доканчивается уже без окна: ответ из фона не должен менять его строку.
            backups.Release.Set();

            Assert.True(backups.Finished.Wait(TimeSpan.FromSeconds(5)), "копия не докончилась");

            // Ответ из фона приходит ЧЕРЕЗ диспетчер: без прокрутки очереди он не выполнится
            // вовсе, и проверка «закрытое окно не трогают» была бы слепой (так и вышло на мутации
            // «убрать `_closed`»: она выжила, пока здесь стоял один `Settle`).
            var processed = false;

            for (var attempt = 0; attempt < 100 && !processed; attempt++)
            {
                PanelTestStand.Settle();
                processed = !window.Busy;
                if (!processed) System.Threading.Thread.Sleep(10);
            }

            Assert.True(processed, "ответ из фона так и не дошёл до окна — проверять нечего");

            Assert.Equal(whileBusy, window.Status);
        }
        finally
        {
            backups.Release.Set();
            PanelTestStand.Settle();
            window.Close();
            backups.Release.Dispose();
            backups.Started.Dispose();
            backups.Finished.Dispose();
        }
    }

    /// <summary>
    /// САМА ДВЕРЬ: ЗАГОЛОВОК-ВОПРОС, ДВА РАЗНЫХ ОТВЕТА, БЕЗОПАСНЫЙ ПО УМОЛЧАНИЮ И ФОКУС НА НЁМ.
    ///
    /// Кадром этого не доказать (съёмка берёт клиентскую область, заголовка окна в ней нет),
    /// поэтому — прогон: заголовок спрашивает, ответы называют РАЗНОЕ следствие, `IsDefault`
    /// не назначен ни одной кнопке (`Ш-54`), фокус при открытии — на безопасном, Esc даёт
    /// «остаться», крестик — тоже (ответ по умолчанию).
    /// </summary>
    [AvaloniaFact]
    public void Заголовок_вопрос_ответы_разные_и_безопасный_по_умолчанию()
    {
        var window = new BusyCloseWindow();
        var leave = window.FindControl<Button>("LeaveButton");
        var stay = window.FindControl<Button>("StayButton");

        Assert.NotNull(leave);
        Assert.NotNull(stay);

        Assert.Equal(PanelStrings.BackupBusyCloseTitle, window.Title);
        Assert.EndsWith("?", window.Title);

        Assert.Equal(PanelStrings.BackupBusyCloseYes, leave!.Content);
        Assert.Equal(PanelStrings.BackupBusyCloseNo, stay!.Content);
        Assert.NotEqual(leave.Content, stay.Content);

        Assert.False(leave.IsDefault);
        Assert.False(stay.IsDefault);

        // До ответа — безопасное «остаться»: закрытое крестиком окно ничего не решило.
        Assert.Equal(BusyCloseChoice.Stay, window.Choice);

        window.Show();
        PanelTestStand.Settle();

        Assert.True(stay.IsFocused, "фокус при открытии обязан быть на безопасном ответе");

        var closed = 0;
        window.Closed += (_, _) => closed++;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal(1, closed);
        Assert.Equal(BusyCloseChoice.Stay, window.Choice);
    }}
