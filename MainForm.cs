using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Панель управления сервером. Вид и набор функций повторяют прежнюю
/// PowerShell-панель; добавлены блок «Баланс» и уход в трей.
/// Все подписи берутся из словарей (Loc), цвета, шрифты и отступы — только из темы
/// (<see cref="Theme"/>): своих литералов цвета и размеров шрифта в этом окне больше нет.
/// Иначе окна снова разъедутся по виду, как разъехались до 1.22.0
/// (техническая спека — _review/ui-1.22-spec.md).
/// </summary>
public sealed class MainForm : Form
{
    private readonly TrayHost _host;

    /// <summary>Подсказки к кнопкам главного окна: что именно сделает каждая.</summary>
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };

    private readonly Label _dot = new();
    private readonly Label _state = new();
    private readonly Label _info = new();
    private readonly Label _peak = new();
    private readonly Label _windows = new();

    // Главная кнопка окна — отдельным типом: по нему Theme узнаёт, какую кнопку заливать
    // акцентом, когда тема применяется ко всему окну целиком (Theme.Apply идёт по дереву
    // контролов и о кнопках ничего не спрашивает, кроме их типа).
    private readonly Button _start = new Theme.PrimaryButton();
    private readonly Button _restart = new();
    private readonly Button _stop = new();
    private readonly Button _open = new();
    private readonly Button _log = new();
    private readonly Button _folder = new();
    private readonly Button _settingsButton = new();
    private readonly Button _backupsButton = new();
    private readonly Button _balanceRefresh = new();
    private readonly Button _balancePlatform = new();

    private readonly CheckBox _chkBrowser = new();
    private readonly CheckBox _chkAutostart = new();

    private readonly Label _balanceBig = new();
    private readonly Label _balanceState = new();
    private readonly Label _balanceChecked = new();

    private bool _suppressAutostart;
    private bool _suppressBrowser;
    private bool _busy;

    public MainForm(TrayHost host)
    {
        _host = host;

        Text = AppVersion.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 596);
        // Шрифт окна — роль Body: тот же девятый кегль, что и раньше, но теперь он
        // не «магическое число» в окне, а роль из темы.
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Font;
        ShowInTaskbar = true;

        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) Icon = icon;
        }
        catch
        {
            // Иконка окна — украшение.
        }

        BuildLayout();
        WireEvents();

        // Тема — последней: BuildLayout расставляет роли шрифтов у меток, а Theme.Apply
        // обходит дерево целиком и приводит цвета, рамки и вид кнопок к одной палитре.
        Theme.Apply(this);

        // Значки на кнопках ставятся один раз: Retext() пересобирает разметку, но сами кнопки
        // остаются теми же объектами, поэтому повторная установка только плодила бы картинки.
        Glyphs.Attach(_settingsButton, Glyphs.Settings, Theme.Colors.Muted);
        Glyphs.Attach(_backupsButton, Glyphs.Backups, Theme.Colors.Muted);

        // Галочка показывает сохранённую настройку, а не значение по умолчанию.
        _suppressBrowser = true;
        _chkBrowser.Checked = _host.Settings.OpenBrowserOnStart;
        _suppressBrowser = false;
    }

    public bool Busy => _busy;

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = AppVersion.Title,
            Font = Theme.Title,
            Location = new Point(16, 12),
            // Ширина по тексту: фиксированная рамка залезала под кнопку «Резервные копии»
            // (с версией в заголовке текст стал длиннее).
            AutoSize = true,
        };
        Controls.Add(title);

        var subtitle = new Label
        {
            Text = Loc.T("main.subtitle"),
            Font = Theme.Hint,
            ForeColor = Theme.Colors.Muted,
            // Поле окна — 16 со всех сторон (шкала темы), поэтому подпись стоит под
            // заголовком по одной с ним линии, а не на два пикселя правее.
            Location = new Point(Theme.WindowPadding, 40),
            // Ширина по тексту: фиксированная рамка залезала под кнопку «Резервные копии».
            // Подпись держим короче ~200px: до кнопки 214px, и «Управление агентом DeepSeek
            // Harness» (220px) уже налезало на неё в русском — это ловит --layout-check.
            AutoSize = true,
        };
        Controls.Add(subtitle);

        _settingsButton.Text = Loc.T("main.settings");
        _settingsButton.Location = new Point(390, 16);
        _settingsButton.Size = new Size(112, Theme.ButtonHeight);
        Theme.Button(_settingsButton, Theme.ButtonKind.Secondary);
        Controls.Add(_settingsButton);

        // Копии — рядом с настройками: из трея их видно, а из панели раньше не было.
        _backupsButton.Text = Loc.T("main.backups");
        _backupsButton.Location = new Point(232, 16);
        _backupsButton.Size = new Size(150, Theme.ButtonHeight);
        Theme.Button(_backupsButton, Theme.ButtonKind.Secondary);
        Controls.Add(_backupsButton);

        // Карточка вместо GroupBox: «модульный» вид давала именно системная рамка группы.
        // Заголовок карточки — обычная метка ролью Heading в её верхней строке (у GroupBox
        // подпись рисовалась поверх рамки), поэтому координаты содержимого не меняются.
        var stateCard = new Theme.CardPanel
        {
            Location = new Point(12, 64),
            Size = new Size(496, 144),
        };
        Controls.Add(stateCard);

        stateCard.Controls.Add(new Label
        {
            Text = Loc.T("main.groupState"),
            Font = Theme.Heading,
            ForeColor = Theme.Colors.Muted,
            Location = new Point(12, 0),
            AutoSize = true,
        });

        _dot.Text = "\u25CF";
        _dot.Font = Theme.Title;
        _dot.Location = new Point(12, 22);
        _dot.Size = new Size(28, 30);
        stateCard.Controls.Add(_dot);

        _state.Text = Loc.T("common.checking");
        _state.Font = Theme.Heading;
        _state.Location = new Point(40, 24);
        _state.Size = new Size(430, 24);
        stateCard.Controls.Add(_state);

        _info.Text = "";
        _info.Font = Theme.Body;
        _info.ForeColor = Theme.Colors.Muted;
        _info.Location = new Point(16, 54);
        _info.Size = new Size(460, 22);
        stateCard.Controls.Add(_info);

        _peak.Text = Loc.T("main.tariffChecking");
        _peak.Font = Theme.BodyBold;
        _peak.Location = new Point(16, 78);
        _peak.Size = new Size(460, 22);
        stateCard.Controls.Add(_peak);

        _windows.Text = "";
        _windows.Font = Theme.Hint;
        _windows.ForeColor = Theme.Colors.Faint;
        _windows.Location = new Point(16, 100);
        _windows.Size = new Size(460, 36);
        stateCard.Controls.Add(_windows);

        // «Запустить» — единственное главное действие окна: акцентная заливка и на два
        // пикселя больше остальных кнопок (Theme.PrimaryButtonHeight).
        _start.Text = Loc.T("main.start");
        _start.Location = new Point(12, 218);
        _start.Size = new Size(160, Theme.PrimaryButtonHeight);
        Theme.Button(_start, Theme.ButtonKind.Primary);
        Controls.Add(_start);

        _restart.Text = Loc.T("main.restart");
        _restart.Location = new Point(180, 218);
        _restart.Size = new Size(160, Theme.ButtonHeight);
        Theme.Button(_restart, Theme.ButtonKind.Secondary);
        Controls.Add(_restart);

        _stop.Text = Loc.T("main.stop");
        _stop.Location = new Point(348, 218);
        _stop.Size = new Size(160, Theme.ButtonHeight);
        Theme.Button(_stop, Theme.ButtonKind.Secondary);
        Controls.Add(_stop);

        _open.Text = Loc.T("main.open");
        _open.Location = new Point(12, 264);
        _open.Size = new Size(160, Theme.ButtonHeight);
        Theme.Button(_open, Theme.ButtonKind.Secondary);
        Controls.Add(_open);

        _log.Text = Loc.T("main.log");
        _log.Location = new Point(180, 264);
        _log.Size = new Size(160, Theme.ButtonHeight);
        Theme.Button(_log, Theme.ButtonKind.Secondary);
        Controls.Add(_log);

        _folder.Text = Loc.T("main.dshFolder");
        _folder.Location = new Point(348, 264);
        _folder.Size = new Size(160, Theme.ButtonHeight);
        Theme.Button(_folder, Theme.ButtonKind.Secondary);
        Controls.Add(_folder);

        _chkBrowser.Text = Loc.T("main.openBrowser");
        _chkBrowser.Checked = true;
        _chkBrowser.Location = new Point(16, 306);
        _chkBrowser.Size = new Size(320, 22);
        Controls.Add(_chkBrowser);

        _chkAutostart.Text = Loc.T("main.autostart");
        _chkAutostart.Location = new Point(16, 330);
        _chkAutostart.Size = new Size(420, 22);
        Controls.Add(_chkAutostart);

        var balanceCard = new Theme.CardPanel
        {
            Location = new Point(12, 362),
            Size = new Size(496, 148),
        };
        Controls.Add(balanceCard);

        balanceCard.Controls.Add(new Label
        {
            Text = Loc.T("main.groupBalance"),
            Font = Theme.Heading,
            ForeColor = Theme.Colors.Muted,
            Location = new Point(12, 0),
            AutoSize = true,
        });

        _balanceBig.Text = Loc.T("common.dash");
        _balanceBig.Font = Theme.Title;
        // Высота 32: роль Title (15pt) требует 28px — в прежние 26px при 14pt текст влезал,
        // а теперь обрезался бы (это и поймал --layout-check). Кегль не уменьшаем.
        _balanceBig.Location = new Point(16, 24);
        _balanceBig.Size = new Size(460, 32);
        balanceCard.Controls.Add(_balanceBig);

        _balanceState.Text = Loc.T("common.checking");
        _balanceState.Font = Theme.Hint;
        _balanceState.ForeColor = Theme.Colors.Faint;
        _balanceState.Location = new Point(16, 60);
        _balanceState.Size = new Size(460, 22);
        balanceCard.Controls.Add(_balanceState);

        _balanceRefresh.Text = Loc.T("main.refreshBalance");
        _balanceRefresh.Location = new Point(16, 92);
        _balanceRefresh.Size = new Size(150, Theme.ButtonHeight);
        Theme.Button(_balanceRefresh, Theme.ButtonKind.Secondary);
        balanceCard.Controls.Add(_balanceRefresh);

        _balancePlatform.Text = Loc.T("main.platform");
        _balancePlatform.Location = new Point(176, 92);
        _balancePlatform.Size = new Size(140, Theme.ButtonHeight);
        Theme.Button(_balancePlatform, Theme.ButtonKind.Secondary);
        balanceCard.Controls.Add(_balancePlatform);

        _balanceChecked.Text = "";
        _balanceChecked.Font = Theme.Hint;
        _balanceChecked.ForeColor = Theme.Colors.Faint;
        _balanceChecked.TextAlign = ContentAlignment.MiddleRight;
        _balanceChecked.Location = new Point(326, 96);
        _balanceChecked.Size = new Size(154, 22);
        balanceCard.Controls.Add(_balanceChecked);

        var hint = new Label
        {
            Text = Loc.T("main.hint"),
            Font = Theme.Hint,
            ForeColor = Theme.Colors.Faint,
            Location = new Point(16, 518),
            Size = new Size(492, 62),
        };
        Controls.Add(hint);

        // Подсказки к кнопкам: по названию не всегда понятно, что именно произойдёт
        // (чем «Перезапустить» отличается от «Остановить», что за «Папка данных DSH»).
        // Ставим их здесь, а не в конструкторе: Retext() пересобирает окно при смене языка,
        // и подсказки должны меняться вместе с подписями.
        _tips.SetToolTip(_start, Loc.T("tip.main.start"));
        _tips.SetToolTip(_restart, Loc.T("tip.main.restart"));
        _tips.SetToolTip(_stop, Loc.T("tip.main.stop"));
        _tips.SetToolTip(_open, Loc.T("tip.main.open"));
        _tips.SetToolTip(_log, Loc.T("tip.main.log"));
        _tips.SetToolTip(_folder, Loc.T("tip.main.folder"));
        _tips.SetToolTip(_backupsButton, Loc.T("tip.main.backups"));
        _tips.SetToolTip(_settingsButton, Loc.T("tip.main.settings"));
        _tips.SetToolTip(_balanceRefresh, Loc.T("tip.main.refresh"));
        _tips.SetToolTip(_balancePlatform, Loc.T("tip.main.account"));
        _tips.SetToolTip(_balanceBig, Loc.T("tip.main.balance"));
        _tips.SetToolTip(_balanceState, Loc.T("tip.main.balance"));
        _tips.SetToolTip(_chkBrowser, Loc.T("tip.main.openBrowser"));
        _tips.SetToolTip(_chkAutostart, Loc.T("tip.main.autostart"));
        _tips.SetToolTip(_state, Loc.T("tip.main.state"));
        _tips.SetToolTip(_peak, Loc.T("tip.main.peak"));
    }

    private void WireEvents()
    {
        _start.Click += (_, _) => _host.RequestStart();
        _restart.Click += async (_, _) => await _host.RequestRestartAsync();
        _stop.Click += async (_, _) => await _host.RequestStopAsync();
        _open.Click += async (_, _) => await _host.RequestOpenBrowserAsync();
        _log.Click += (_, _) => _host.OpenLog();
        _folder.Click += (_, _) => _host.OpenDshFolder();

        _settingsButton.Click += (_, _) => _host.OpenSettings();
        _backupsButton.Click += (_, _) => _host.OpenBackups();

        _chkBrowser.CheckedChanged += (_, _) =>
        {
            if (_suppressBrowser) return;
            _host.Settings.OpenBrowserOnStart = _chkBrowser.Checked;
            _host.SaveSettings();
        };

        _chkAutostart.CheckedChanged += (_, _) =>
        {
            if (_suppressAutostart) return;
            _host.SetAutostart(_chkAutostart.Checked);
        };

        _balanceRefresh.Click += async (_, _) => await _host.RefreshBalanceAsync(force: true);
        _balancePlatform.Click += (_, _) => _host.OpenPlatform();
    }

    /// <summary>Перерисовка состояния: вызывается из TrayHost на потоке интерфейса.
    /// Цвета состояния — роли темы: «работает» — Success, «порт занят другим» и пик — Warning,
    /// остановлено — Muted, отказ получения баланса — Danger.</summary>
    public void ApplyStatus(ServerStatus status, PeakState peak, BalanceResult balance, bool busy, bool autostart)
    {
        _busy = busy;

        if (status.Running)
        {
            _dot.ForeColor = Theme.Colors.Success;
            _state.Text = Loc.T("state.runningTitle");
            _state.ForeColor = Theme.Colors.Success;
            _info.Text = _host.HasAuthenticatedUrl
                ? Loc.T("main.pidUrlKnown", status.Pid)
                : Loc.T("main.pidForeign", status.Pid);
        }
        else if (status.PortBusyByOther)
        {
            _dot.ForeColor = Theme.Colors.Warning;
            _state.Text = Loc.T("state.portBusyTitle", status.Port);
            _state.ForeColor = Theme.Colors.Warning;
            _info.Text = $"{status.ProcessName} (PID {status.Pid}) · {status.Url}";
        }
        else
        {
            _dot.ForeColor = Theme.Colors.Disabled;
            _state.Text = Loc.T("state.stoppedTitle");
            _state.ForeColor = Theme.Colors.Muted;
            _info.Text = Loc.T("main.pressStart");
        }

        _peak.ForeColor = peak.InPeak ? Theme.Colors.Warning : Theme.Colors.Success;
        var peakText = Loc.T("main.tariff", peak.StateText);
        if (!string.IsNullOrEmpty(peak.NextText)) peakText += " · " + peak.NextText;
        _peak.Text = peakText;
        _windows.Text = Loc.T("main.windows", peak.WindowText, peak.Checked);

        ApplyBalance(balance);
        ApplyAutostart(autostart);

        if (!busy)
        {
            _start.Enabled = !status.Running && !status.PortBusyByOther;
            _stop.Enabled = status.Running;
            _restart.Enabled = status.Running;
            _open.Enabled = true;
        }
    }

    private void ApplyBalance(BalanceResult balance)
    {
        if (balance == null)
        {
            _balanceBig.Text = Loc.T("common.dash");
            _balanceState.Text = Loc.T("common.checking");
            _balanceChecked.Text = "";
            return;
        }

        if (balance.Ok)
        {
            var low = _host.IsBalanceLow(balance);
            _balanceBig.Text = balance.Summary;
            _balanceBig.ForeColor = low ? Theme.Colors.Warning : Theme.Colors.Text;
            _balanceState.Text = Loc.T("main.balanceAvailable", Loc.T(balance.Available ? "common.yes" : "common.no"))
                                 + (string.IsNullOrEmpty(balance.Source) ? "" : $" · {balance.Source}")
                                 + (low ? " · " + Loc.T("main.balanceLow", _host.ThresholdText) : "");
            _balanceState.ForeColor = balance.Available ? (low ? Theme.Colors.Warning : Theme.Colors.Success) : Theme.Colors.Warning;
            _balanceChecked.Text = Loc.T("main.checkedAt", balance.CheckedAt.ToString("HH:mm:ss"));
        }
        else
        {
            _balanceBig.Text = Loc.T("common.dash");
            _balanceBig.ForeColor = Theme.Colors.Text;
            _balanceState.Text = balance.Error;
            // Отказ получить баланс — это ошибка, а не предупреждение: роль Danger.
            _balanceState.ForeColor = Theme.Colors.Danger;
            _balanceChecked.Text = Loc.T("main.checkAttempt", balance.CheckedAt.ToString("HH:mm:ss"));
        }
    }

    private void ApplyAutostart(bool enabled)
    {
        _suppressAutostart = true;
        _chkAutostart.Checked = enabled;
        _suppressAutostart = false;
    }

    /// <summary>
    /// Подсказка у галочки автозапуска. Галочка отвечает на вопрос «автозапуск включён?», и при
    /// записи на чужую копию она честно стоит — но человеку надо знать, что запустится не эта
    /// панель: это ровно та путаница, из-за которой после перезагрузки поднималась старая копия.
    /// </summary>
    public void SetAutostartNote(Autostart.State state) =>
        _tips.SetToolTip(_chkAutostart, state.Where == Autostart.Where.Other
            ? Loc.T("tip.main.autostartOther", state.Path)
            : Loc.T("tip.main.autostart"));

    /// <summary>Показывает в галочке сохранённую настройку (после окна настроек).</summary>
    public void ApplyOpenBrowser(bool enabled)
    {
        _suppressBrowser = true;
        _chkBrowser.Checked = enabled;
        _suppressBrowser = false;
    }

    public void SetBusy(string text)
    {
        _busy = true;
        SetButtonsEnabled(false);
        Cursor = Cursors.WaitCursor;
        if (!string.IsNullOrEmpty(text)) _info.Text = text;
        Refresh();
    }

    public void ClearBusy()
    {
        _busy = false;
        SetButtonsEnabled(true);
        Cursor = Cursors.Default;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _start, _restart, _stop, _open, _log, _folder, _settingsButton, _backupsButton, _balanceRefresh, _balancePlatform, _chkAutostart })
        {
            control.Enabled = enabled;
        }
    }

    /// <summary>
    /// Перенабирает подписи после смены языка: окно перестраивается из тех же полей,
    /// обработчики остаются на месте (их вешает WireEvents один раз).
    /// Тема применяется здесь второй раз — Retext собирает окно заново, и без этого
    /// после смены языка вид вернулся бы к системным цветам (отдельный пункт приёмки 1.22.0).
    /// </summary>
    public void Retext()
    {
        SuspendLayout();
        try
        {
            Text = AppVersion.Title;
            Controls.Clear();
            BuildLayout();
            Theme.Apply(this);

            // Галочка показывает сохранённую настройку, а не значение по умолчанию.
            _suppressBrowser = true;
            _chkBrowser.Checked = _host.Settings.OpenBrowserOnStart;
            _suppressBrowser = false;
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_host.Exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _host.HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
            _host.HideToTray();
        }
    }
}
