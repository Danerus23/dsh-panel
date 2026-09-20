using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Панель управления сервером. Вид и набор функций повторяют прежнюю
/// PowerShell-панель; добавлены блок «Баланс» и уход в трей.
/// Все подписи берутся из словарей (Loc), шрифт — с учётом языка.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Green = Color.FromArgb(34, 197, 94);
    private static readonly Color GreenText = Color.FromArgb(21, 128, 61);
    private static readonly Color Amber = Color.FromArgb(234, 179, 8);
    private static readonly Color AmberText = Color.FromArgb(161, 98, 7);
    private static readonly Color Grey = Color.FromArgb(148, 163, 184);
    private static readonly Color GreyText = Color.FromArgb(71, 85, 105);
    private static readonly Color Muted = Color.FromArgb(110, 110, 110);
    private static readonly Color Faint = Color.FromArgb(130, 130, 130);
    private static readonly Color OrangeText = Color.FromArgb(180, 83, 9);

    private readonly TrayHost _host;

    /// <summary>Подсказки к кнопкам главного окна: что именно сделает каждая.</summary>
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };

    private readonly Label _dot = new();
    private readonly Label _state = new();
    private readonly Label _info = new();
    private readonly Label _peak = new();
    private readonly Label _windows = new();

    private readonly Button _settingsButton = new();
    private readonly Button _backupsButton = new();
    private readonly Button _start = new();
    private readonly Button _restart = new();
    private readonly Button _stop = new();
    private readonly Button _open = new();
    private readonly Button _log = new();
    private readonly Button _folder = new();
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
        Font = Loc.UiFont(9F);
        BackColor = Color.White;
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

        // Значки на кнопках ставятся один раз: Retext() пересобирает разметку, но сами кнопки
        // остаются теми же объектами, поэтому повторная установка только плодила бы картинки.
        Glyphs.Attach(_settingsButton, Glyphs.Settings, Muted);
        Glyphs.Attach(_backupsButton, Glyphs.Backups, Muted);

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
            Font = Loc.UiFont(13F, FontStyle.Bold),
            Location = new Point(16, 12),
            // Ширина по тексту: фиксированная рамка залезала под кнопку «Резервные копии»
            // (с версией в заголовке текст стал длиннее).
            AutoSize = true,
        };
        Controls.Add(title);

        var subtitle = new Label
        {
            Text = Loc.T("main.subtitle"),
            ForeColor = Muted,
            Location = new Point(18, 40),
            // Ширина по тексту: фиксированная рамка залезала под кнопку «Резервные копии».
            // Подпись держим короче ~200px: до кнопки 214px, и «Управление агентом DeepSeek
            // Harness» (220px) уже налезало на неё в русском — это ловит --layout-check.
            AutoSize = true,
        };
        Controls.Add(subtitle);

        _settingsButton.Text = Loc.T("main.settings");
        _settingsButton.Location = new Point(390, 16);
        _settingsButton.Size = new Size(112, 30);
        Controls.Add(_settingsButton);

        // Копии — рядом с настройками: из трея их видно, а из панели раньше не было.
        _backupsButton.Text = Loc.T("main.backups");
        _backupsButton.Location = new Point(232, 16);
        _backupsButton.Size = new Size(150, 30);
        Controls.Add(_backupsButton);

        var stateBox = new GroupBox
        {
            Text = Loc.T("main.groupState"),
            Location = new Point(12, 64),
            Size = new Size(496, 144),
        };
        Controls.Add(stateBox);

        _dot.Text = "\u25CF";
        _dot.Font = Loc.UiFont(14F);
        _dot.Location = new Point(12, 22);
        _dot.Size = new Size(28, 30);
        stateBox.Controls.Add(_dot);

        _state.Text = Loc.T("common.checking");
        _state.Font = Loc.UiFont(11F, FontStyle.Bold);
        _state.Location = new Point(40, 24);
        _state.Size = new Size(430, 24);
        stateBox.Controls.Add(_state);

        _info.Text = "";
        _info.ForeColor = Muted;
        _info.Location = new Point(16, 54);
        _info.Size = new Size(460, 22);
        stateBox.Controls.Add(_info);

        _peak.Text = Loc.T("main.tariffChecking");
        _peak.Font = Loc.UiFont(9F, FontStyle.Bold);
        _peak.Location = new Point(16, 78);
        _peak.Size = new Size(460, 22);
        stateBox.Controls.Add(_peak);

        _windows.Text = "";
        _windows.ForeColor = Faint;
        _windows.Location = new Point(16, 100);
        _windows.Size = new Size(460, 36);
        stateBox.Controls.Add(_windows);

        _start.Text = Loc.T("main.start");
        _start.Location = new Point(12, 218);
        _start.Size = new Size(160, 38);
        Controls.Add(_start);

        _restart.Text = Loc.T("main.restart");
        _restart.Location = new Point(180, 218);
        _restart.Size = new Size(160, 38);
        Controls.Add(_restart);

        _stop.Text = Loc.T("main.stop");
        _stop.Location = new Point(348, 218);
        _stop.Size = new Size(160, 38);
        Controls.Add(_stop);

        _open.Text = Loc.T("main.open");
        _open.Location = new Point(12, 264);
        _open.Size = new Size(160, 32);
        Controls.Add(_open);

        _log.Text = Loc.T("main.log");
        _log.Location = new Point(180, 264);
        _log.Size = new Size(160, 32);
        Controls.Add(_log);

        _folder.Text = Loc.T("main.dshFolder");
        _folder.Location = new Point(348, 264);
        _folder.Size = new Size(160, 32);
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

        var balanceBox = new GroupBox
        {
            Text = Loc.T("main.groupBalance"),
            Location = new Point(12, 362),
            Size = new Size(496, 148),
        };
        Controls.Add(balanceBox);

        _balanceBig.Text = Loc.T("common.dash");
        _balanceBig.Font = Loc.UiFont(14F, FontStyle.Bold);
        _balanceBig.Location = new Point(16, 24);
        _balanceBig.Size = new Size(460, 26);
        balanceBox.Controls.Add(_balanceBig);

        _balanceState.Text = Loc.T("common.checking");
        _balanceState.ForeColor = Faint;
        _balanceState.Location = new Point(16, 56);
        _balanceState.Size = new Size(460, 22);
        balanceBox.Controls.Add(_balanceState);

        _balanceRefresh.Text = Loc.T("main.refreshBalance");
        _balanceRefresh.Location = new Point(16, 92);
        _balanceRefresh.Size = new Size(150, 30);
        balanceBox.Controls.Add(_balanceRefresh);

        _balancePlatform.Text = Loc.T("main.platform");
        _balancePlatform.Location = new Point(176, 92);
        _balancePlatform.Size = new Size(140, 30);
        balanceBox.Controls.Add(_balancePlatform);

        _balanceChecked.Text = "";
        _balanceChecked.ForeColor = Faint;
        _balanceChecked.TextAlign = ContentAlignment.MiddleRight;
        _balanceChecked.Location = new Point(326, 96);
        _balanceChecked.Size = new Size(154, 22);
        balanceBox.Controls.Add(_balanceChecked);

        var hint = new Label
        {
            Text = Loc.T("main.hint"),
            ForeColor = Faint,
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

    /// <summary>Перерисовка состояния: вызывается из TrayHost на потоке интерфейса.</summary>
    public void ApplyStatus(ServerStatus status, PeakState peak, BalanceResult balance, bool busy, bool autostart)
    {
        _busy = busy;

        if (status.Running)
        {
            _dot.ForeColor = Green;
            _state.Text = Loc.T("state.runningTitle");
            _state.ForeColor = GreenText;
            _info.Text = _host.HasAuthenticatedUrl
                ? Loc.T("main.pidUrlKnown", status.Pid)
                : Loc.T("main.pidForeign", status.Pid);
        }
        else if (status.PortBusyByOther)
        {
            _dot.ForeColor = Amber;
            _state.Text = Loc.T("state.portBusyTitle", status.Port);
            _state.ForeColor = AmberText;
            _info.Text = $"{status.ProcessName} (PID {status.Pid}) · {status.Url}";
        }
        else
        {
            _dot.ForeColor = Grey;
            _state.Text = Loc.T("state.stoppedTitle");
            _state.ForeColor = GreyText;
            _info.Text = Loc.T("main.pressStart");
        }

        _peak.ForeColor = peak.InPeak ? OrangeText : GreenText;
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
            _balanceBig.ForeColor = low ? AmberText : Color.Black;
            _balanceState.Text = Loc.T("main.balanceAvailable", Loc.T(balance.Available ? "common.yes" : "common.no"))
                                 + (string.IsNullOrEmpty(balance.Source) ? "" : $" · {balance.Source}")
                                 + (low ? " · " + Loc.T("main.balanceLow", _host.ThresholdText) : "");
            _balanceState.ForeColor = balance.Available ? (low ? AmberText : GreenText) : AmberText;
            _balanceChecked.Text = Loc.T("main.checkedAt", balance.CheckedAt.ToString("HH:mm:ss"));
        }
        else
        {
            _balanceBig.Text = Loc.T("common.dash");
            _balanceBig.ForeColor = Color.Black;
            _balanceState.Text = balance.Error;
            _balanceState.ForeColor = AmberText;
            _balanceChecked.Text = Loc.T("main.checkAttempt", balance.CheckedAt.ToString("HH:mm:ss"));
        }
    }

    private void ApplyAutostart(bool enabled)
    {
        _suppressAutostart = true;
        _chkAutostart.Checked = enabled;
        _suppressAutostart = false;
    }

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
    /// </summary>
    public void Retext()
    {
        SuspendLayout();
        try
        {
            Text = AppVersion.Title;
            Controls.Clear();
            BuildLayout();

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
