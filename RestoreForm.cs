using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Окно «Восстановление из копии». Показывает, что лежит в архиве и куда это ляжет,
/// даёт выбрать состав и накладывает копию. Перед накатом панель делает копию текущего
/// состояния, поэтому неудачный накат можно откатить.
///
/// Ключи и движок возвращаются только по явной галочке: первое — приватные файлы,
/// второе — сотни мегабайт, которые на машине с установленным Node не нужны.
/// </summary>
public sealed class RestoreForm : Form
{
    private static readonly Color Muted = Color.FromArgb(110, 110, 110);
    private static readonly Color Faint = Color.FromArgb(130, 130, 130);
    private static readonly Color GreenText = Color.FromArgb(21, 128, 61);
    private static readonly Color AmberText = Color.FromArgb(161, 98, 7);
    private static readonly Color RedText = Color.FromArgb(185, 28, 28);

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly BackupService _backups;
    private readonly Func<bool> _serverRunning;
    private readonly Func<bool> _stopServer;

    private readonly TextBox _txtArchive = new();
    private readonly Button _btnPick = new();
    private readonly Button _btnCheck = new();
    private readonly Label _lblOrigin = new();
    private readonly Label _lblContentsTitle = new();
    private readonly Label _lblContents = new();
    private readonly Label _lblKeysWarning = new();
    private readonly Label _lblWhatTitle = new();
    private readonly CheckBox _chkDshHome = new();
    private readonly CheckBox _chkPanel = new();
    private readonly CheckBox _chkKeys = new();
    private readonly CheckBox _chkEngine = new();
    private readonly CheckBox _chkSafety = new();
    private readonly Label _lblServer = new();
    private readonly Button _btnStopServer = new();
    private readonly Button _btnRestore = new();
    private readonly TextBox _txtReport = new();
    private readonly Button _btnClose = new();

    private RestorePlan _plan;
    private bool _busy;

    public RestoreForm(AppPaths paths, AppSettings settings, BackupService backups, Func<bool> serverRunning, Func<bool> stopServer, string preselected = null)
    {
        _paths = paths;
        _settings = settings;
        _backups = backups;
        _serverRunning = serverRunning;
        _stopServer = stopServer;

        Text = Loc.T("restore.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 560);
        MinimumSize = new Size(636, 600);
        Font = Loc.UiFont(9F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        LoadArchive(preselected);
    }

    // --- вид ---------------------------------------------------------------

    private void BuildLayout()
    {
        var lblArchive = new Label
        {
            Text = Loc.T("restore.archive"),
            Location = new Point(16, 19),
            Size = new Size(62, 20),
        };
        Controls.Add(lblArchive);

        _txtArchive.ReadOnly = true;
        _txtArchive.BackColor = Color.White;
        _txtArchive.Location = new Point(80, 15);
        _txtArchive.Size = new Size(384, 24);
        _txtArchive.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_txtArchive);

        _btnPick.Text = Loc.T("restore.pick");
        _btnPick.Location = new Point(472, 14);
        _btnPick.Size = new Size(132, 26);
        _btnPick.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnPick.Click += (_, _) => PickArchive();
        Controls.Add(_btnPick);

        _btnCheck.Text = Loc.T("restore.check");
        _btnCheck.Location = new Point(80, 46);
        _btnCheck.Size = new Size(160, 26);
        _btnCheck.Click += async (_, _) => await CheckAsync();
        Controls.Add(_btnCheck);

        _lblOrigin.ForeColor = Muted;
        _lblOrigin.Location = new Point(16, 82);
        _lblOrigin.Size = new Size(588, 20);
        _lblOrigin.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_lblOrigin);

        _lblContentsTitle.Text = Loc.T("restore.contents");
        _lblContentsTitle.Location = new Point(16, 108);
        _lblContentsTitle.Size = new Size(588, 18);
        Controls.Add(_lblContentsTitle);

        _lblContents.ForeColor = Muted;
        _lblContents.Location = new Point(16, 128);
        _lblContents.Size = new Size(588, 66);
        _lblContents.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_lblContents);

        _lblKeysWarning.ForeColor = RedText;
        _lblKeysWarning.Location = new Point(16, 196);
        _lblKeysWarning.Size = new Size(588, 32);
        _lblKeysWarning.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_lblKeysWarning);

        _lblWhatTitle.Text = Loc.T("restore.whatToRestore");
        _lblWhatTitle.Location = new Point(16, 236);
        _lblWhatTitle.Size = new Size(588, 18);
        Controls.Add(_lblWhatTitle);

        var boxes = new (CheckBox Box, string Key, int Top)[]
        {
            (_chkDshHome, "restore.opt.dshHome", 258),
            (_chkPanel, "restore.opt.panel", 282),
            (_chkKeys, "restore.opt.keys", 306),
            (_chkEngine, "restore.opt.engine", 330),
            (_chkSafety, "restore.opt.safety", 354),
        };

        foreach (var (box, key, top) in boxes)
        {
            box.Text = Loc.T(key);
            box.Location = new Point(16, top);
            box.Size = new Size(588, 22);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(box);
        }

        _chkDshHome.Checked = true;
        _chkPanel.Checked = true;
        _chkEngine.Checked = true;
        _chkSafety.Checked = true;

        _lblServer.Location = new Point(16, 388);
        _lblServer.Size = new Size(440, 32);
        Controls.Add(_lblServer);

        _btnStopServer.Text = Loc.T("restore.stopServer");
        _btnStopServer.Location = new Point(470, 386);
        _btnStopServer.Size = new Size(134, 28);
        _btnStopServer.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnStopServer.Click += (_, _) => StopServer();
        Controls.Add(_btnStopServer);

        _btnRestore.Text = Loc.T("restore.start");
        _btnRestore.Location = new Point(16, 424);
        _btnRestore.Size = new Size(190, 32);
        _btnRestore.Click += async (_, _) => await RestoreAsync();
        Controls.Add(_btnRestore);

        _btnClose.Text = Loc.T("restore.close");
        _btnClose.DialogResult = DialogResult.Cancel;
        _btnClose.Location = new Point(502, 424);
        _btnClose.Size = new Size(102, 32);
        _btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(_btnClose);

        _txtReport.Multiline = true;
        _txtReport.ReadOnly = true;
        _txtReport.ScrollBars = ScrollBars.Vertical;
        _txtReport.BackColor = Color.White;
        _txtReport.BorderStyle = BorderStyle.FixedSingle;
        _txtReport.Location = new Point(16, 466);
        _txtReport.Size = new Size(588, 78);
        _txtReport.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_txtReport);

        CancelButton = _btnClose;
    }

    // --- разбор архива ------------------------------------------------------

    /// <summary>Выбирает архив (из списка копий или файлом) и показывает, что в нём.</summary>
    private void LoadArchive(string path)
    {
        _plan = null;
        _chkKeys.Checked = false;
        _chkKeys.Enabled = false;
        _chkEngine.Enabled = false;
        _lblContents.Text = "";
        _lblKeysWarning.Text = "";
        _btnRestore.Enabled = false;

        if (string.IsNullOrWhiteSpace(path))
        {
            // Без выбранного архива показываем самый свежий — обычный случай «вернуть вчерашнее».
            path = _backups.List().FirstOrDefault()?.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _txtArchive.Text = "";
                _lblOrigin.Text = Loc.T("restore.needArchive");
                UpdateServerLine();
                return;
            }
        }

        _txtArchive.Text = path;
        _plan = RestoreService.Plan(path);

        if (!_plan.Ok)
        {
            _lblOrigin.ForeColor = RedText;
            _lblOrigin.Text = Loc.T("restore.planFailed", _plan.Error);
            UpdateServerLine();
            return;
        }

        _lblOrigin.ForeColor = Muted;
        _lblOrigin.Text = Loc.T("restore.origin", _plan.Origin());
        _lblContents.Text = string.Join(Environment.NewLine, _plan.Contents());

        _chkDshHome.Enabled = _plan.HasDshHome;
        _chkPanel.Enabled = _plan.HasPanel;
        _chkKeys.Enabled = _plan.HasKeys;
        _chkEngine.Enabled = _plan.HasEngine;

        _chkDshHome.Checked = _plan.HasDshHome;
        _chkPanel.Checked = _plan.HasPanel;
        _chkEngine.Checked = _plan.HasEngine;

        _lblKeysWarning.Text = _plan.HasKeys ? Loc.T("restore.keysWarning") : "";
        _btnRestore.Enabled = true;
        UpdateServerLine();
    }

    private void PickArchive()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = Loc.T("restore.title"),
                Filter = "*.zip|*.zip|*.*|*.*",
                CheckFileExists = true,
            };

            var current = _txtArchive.Text;
            if (!string.IsNullOrWhiteSpace(current))
            {
                var folder = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;
            }
            else if (Directory.Exists(_backups.Folder))
            {
                dialog.InitialDirectory = _backups.Folder;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK) LoadArchive(dialog.FileName);
        }
        catch (Exception error)
        {
            Report(error.Message);
        }
    }

    /// <summary>Сверка контрольных сумм до наката — чтобы не накладывать битый архив.</summary>
    private async Task CheckAsync()
    {
        if (_plan == null || !_plan.Ok)
        {
            Report(Loc.T("restore.needArchive"));
            return;
        }

        if (_busy) return;
        Busy(true, Loc.T("backup.checking", _plan.Name));

        try
        {
            var path = _plan.Path;
            var check = await Task.Run(() => BackupService.Verify(path));
            Report(check.Summary() + Environment.NewLine + Loc.T(check.Ok ? "cli.valid" : "cli.invalid"));
        }
        catch (Exception error)
        {
            Report(error.Message);
        }
        finally
        {
            Busy(false, null);
        }
    }

    // --- накат --------------------------------------------------------------

    private async Task RestoreAsync()
    {
        if (_busy) return;

        if (_plan == null || !_plan.Ok)
        {
            Report(Loc.T("restore.needArchive"));
            return;
        }

        var options = new RestoreOptions
        {
            DshHome = _chkDshHome.Checked,
            PanelSettings = _chkPanel.Checked,
            Keys = _chkKeys.Checked,
            Engine = _chkEngine.Checked,
            SafetyCopy = _chkSafety.Checked,
        };

        if (!options.DshHome && !options.PanelSettings && !options.Keys && !options.Engine)
        {
            Report(Loc.T("restore.needSomething"));
            return;
        }

        // Сервер держит файлы DSH открытыми: накат по работающему серверу — это половина данных.
        if (_serverRunning != null && _serverRunning())
        {
            if (!StopServer())
            {
                Report(Loc.T("restore.noServerStop"));
                return;
            }
        }

        var question = Loc.T("restore.confirm", _plan.Origin()) + (options.Keys ? Loc.T("restore.confirmKeys") : "");
        if (MessageBox.Show(this, question, Loc.T("restore.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        Busy(true, Loc.T("restore.running"));

        try
        {
            var plan = _plan;
            var result = await Task.Run(() => RestoreService.Restore(plan, options, _paths, _settings,
                step => SafeReport(Loc.T("restore.running") + " " + step)));
            AppLog.Write(_paths, "накат копии из окна: " + result.Summary());
            Report(result.Summary() + Environment.NewLine + string.Join(Environment.NewLine, result.Restored.Select(line => "  " + line))
                   + string.Join(Environment.NewLine, result.Skipped.Select(line => "  " + line)));

            if (result.Ok)
            {
                // Настройки на диске могли быть заменены копией — забираем их себе.
                if (options.PanelSettings) _settings.Reload(_paths.SettingsPath);
                LoadArchive(_plan.Path);
            }
        }
        catch (Exception error)
        {
            Report(error.Message);
        }
        finally
        {
            Busy(false, null);
        }
    }

    private bool StopServer()
    {
        try
        {
            var stopped = _stopServer != null && _stopServer();
            UpdateServerLine();
            return stopped;
        }
        catch (Exception error)
        {
            Report(error.Message);
            return false;
        }
    }

    private void UpdateServerLine()
    {
        var running = _serverRunning != null && _serverRunning();
        _lblServer.ForeColor = running ? AmberText : GreenText;
        _lblServer.Text = Loc.T(running ? "restore.serverRunning" : "restore.serverStopped");
        _btnStopServer.Enabled = running;
    }

    private void Busy(bool busy, string text)
    {
        _busy = busy;
        _btnRestore.Enabled = !busy && _plan != null && _plan.Ok;
        _btnCheck.Enabled = !busy;
        _btnPick.Enabled = !busy;

        // Во время наката окно не закрываем: работа оборвалась бы на половине, а сообщения
        // о ходе прилетели бы в уничтоженное окно.
        _btnClose.Enabled = !busy;
        ControlBox = !busy;

        UseWaitCursor = busy;
        if (!string.IsNullOrEmpty(text)) Report(text);
    }

    /// <summary>
    /// Отчёт из фоновой задачи. Окно могло закрыться — тогда просто ничего не делаем:
    /// BeginInvoke на уничтоженном окне бросает исключение.
    /// </summary>
    private void SafeReport(string text)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed) Report(text);
            }));
        }
        catch (InvalidOperationException)
        {
            // Окно закрылось между проверкой и вызовом.
        }
    }

    private void Report(string text)
    {
        _txtReport.Text = text.Length > 4000 ? text[^4000..] : text;
        _txtReport.SelectionStart = _txtReport.TextLength;
        _txtReport.ScrollToCaret();
    }
}
