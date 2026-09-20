using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Окно «Резервные копии»: расписание, состав архива, папка назначения и список
/// уже сделанных копий с проверкой. Копия — это zip с manifest.json внутри, по
/// которому инсталлятор на чистой машине разворачивает «нас» обратно.
/// </summary>
public sealed class BackupForm : Form
{
    private static readonly Color Muted = Color.FromArgb(110, 110, 110);
    private static readonly Color Faint = Color.FromArgb(130, 130, 130);
    private static readonly Color GreenText = Color.FromArgb(21, 128, 61);
    private static readonly Color AmberText = Color.FromArgb(161, 98, 7);
    private static readonly Color RedText = Color.FromArgb(185, 28, 28);

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly BackupService _backups;

    /// <summary>Открыть окно наката: панель решает, кого останавливать и что перечитывать.</summary>
    private readonly Action<string> _onRestore;

    private readonly CheckBox _chkAuto = new();
    private readonly Label _lblEvery = new();
    private readonly NumericUpDown _numHours = new();
    private readonly Label _lblHours = new();
    private readonly CheckBox _chkEngine = new();
    private readonly CheckBox _chkSessions = new();
    private readonly Label _lblKeep = new();
    private readonly NumericUpDown _numKeep = new();
    private readonly Label _lblKeepSuffix = new();

    private readonly TextBox _txtFolder = new();
    private readonly CheckBox _chkKeys = new();
    private readonly Button _btnAddKeyDir = new();
    private readonly Label _lblKeysHint = new();

    private readonly ListBox _list = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblLast = new();

    private readonly Button _btnCreate = new();
    private readonly Button _btnRestore = new();
    private readonly Button _btnVerify = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnOpen = new();

    private bool _busy;

    /// <summary>Полоса «работа идёт»: копия с движком собирается минуты.</summary>
    private readonly ProgressBar _bar = new();

    /// <summary>Подсказки к элементам окна: держим полем, иначе сборщик мусора их снимет.</summary>
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };

    public BackupForm(AppSettings settings, AppPaths paths, Action<string> onRestore = null)
    {
        _settings = settings;
        _paths = paths;
        _backups = new BackupService(paths, settings);
        _onRestore = onRestore;

        Text = Loc.T("backup.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        // Окно нарочно просторное: в прежнем размере кнопки внизу налезали друг на друга,
        // а подписи в группах стояли вплотную.
        ClientSize = new Size(640, 700);
        MinimumSize = new Size(656, 740);
        Font = Loc.UiFont(9F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        LoadValues();
    }

    // --- вид ---------------------------------------------------------------

    private void BuildLayout()
    {
        // --- когда и что ---------------------------------------------------
        var when = new GroupBox
        {
            Text = Loc.T("backup.groupWhen"),
            Location = new Point(12, 10),
            Size = new Size(616, 178),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(when);

        _chkAuto.Text = Loc.T("backup.auto");
        _chkAuto.Location = new Point(16, 26);
        _chkAuto.Size = new Size(300, 22);
        when.Controls.Add(_chkAuto);

        _lblEvery.Text = Loc.T("settings.every");
        _lblEvery.ForeColor = Muted;
        _lblEvery.Location = new Point(322, 28);
        _lblEvery.Size = new Size(42, 20);
        _lblEvery.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        when.Controls.Add(_lblEvery);

        _numHours.Minimum = 1;
        _numHours.Maximum = 720;
        _numHours.Value = 24;
        _numHours.Location = new Point(366, 26);
        _numHours.Size = new Size(60, 24);
        _numHours.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        when.Controls.Add(_numHours);

        _lblHours.Text = Loc.T("settings.hours");
        _lblHours.ForeColor = Muted;
        _lblHours.Location = new Point(432, 28);
        _lblHours.Size = new Size(48, 20);
        _lblHours.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        when.Controls.Add(_lblHours);

        _chkEngine.Text = Loc.T("backup.withEngine");
        _chkEngine.Location = new Point(16, 56);
        _chkEngine.Size = new Size(540, 22);
        _chkEngine.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        when.Controls.Add(_chkEngine);

        _chkSessions.Text = Loc.T("backup.withSessions");
        _chkSessions.Location = new Point(16, 82);
        _chkSessions.Size = new Size(400, 22);
        when.Controls.Add(_chkSessions);

        _lblKeep.Text = Loc.T("backup.keepPrefix");
        _lblKeep.ForeColor = Muted;
        _lblKeep.Location = new Point(16, 112);
        _lblKeep.Size = new Size(120, 20);
        when.Controls.Add(_lblKeep);

        _numKeep.Minimum = 1;
        _numKeep.Maximum = 100;
        _numKeep.Value = 5;
        _numKeep.Location = new Point(140, 110);
        _numKeep.Size = new Size(60, 24);
        when.Controls.Add(_numKeep);

        _lblKeepSuffix.Text = Loc.T("backup.keepSuffix");
        _lblKeepSuffix.ForeColor = Muted;
        _lblKeepSuffix.Location = new Point(206, 112);
        _lblKeepSuffix.Size = new Size(200, 20);
        when.Controls.Add(_lblKeepSuffix);

        when.Controls.Add(new Label
        {
            Text = Loc.T("backup.secretHint"),
            ForeColor = AmberText,
            Location = new Point(16, 140),
            Size = new Size(544, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        // --- куда ----------------------------------------------------------
        var where = new GroupBox
        {
            Text = Loc.T("backup.groupWhere"),
            Location = new Point(12, 196),
            Size = new Size(616, 136),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(where);

        where.Controls.Add(new Label
        {
            Text = Loc.T("backup.folder"),
            Location = new Point(16, 28),
            Size = new Size(96, 20),
        });

        _txtFolder.ReadOnly = true;
        _txtFolder.BackColor = Color.White;
        _txtFolder.Location = new Point(114, 26);
        _txtFolder.Size = new Size(248, 24);
        _txtFolder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        where.Controls.Add(_txtFolder);

        var pickFolder = new Button
        {
            Text = Loc.T("backup.change"),
            Location = new Point(370, 25),
            Size = new Size(92, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        pickFolder.Click += (_, _) => PickFolder();
        where.Controls.Add(pickFolder);

        _btnOpen.Text = Loc.T("backup.open");
        _btnOpen.Location = new Point(470, 25);
        _btnOpen.Size = new Size(90, 26);
        _btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnOpen.Click += (_, _) => OpenFolder();
        where.Controls.Add(_btnOpen);

        _chkKeys.Text = Loc.T("onboard.keys.enable");
        _chkKeys.Location = new Point(16, 60);
        _chkKeys.Size = new Size(420, 22);
        _chkKeys.CheckedChanged += (_, _) => SyncKeysHint();
        where.Controls.Add(_chkKeys);

        _btnAddKeyDir.Text = Loc.T("backup.addKeyDir");
        _btnAddKeyDir.Location = new Point(446, 59);
        _btnAddKeyDir.Size = new Size(114, 26);
        _btnAddKeyDir.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnAddKeyDir.Click += (_, _) => AddKeyDirectory();
        where.Controls.Add(_btnAddKeyDir);

        _lblKeysHint.ForeColor = Faint;
        _lblKeysHint.Location = new Point(16, 86);
        _lblKeysHint.Size = new Size(544, 42);
        _lblKeysHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        where.Controls.Add(_lblKeysHint);

        // --- список --------------------------------------------------------
        var listBox = new GroupBox
        {
            Text = Loc.T("backup.groupList"),
            Location = new Point(12, 340),
            Size = new Size(616, 226),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(listBox);

        _list.Location = new Point(16, 24);
        _list.Size = new Size(420, 156);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.IntegralHeight = false;
        _list.DoubleClick += (_, _) => Verify();
        listBox.Controls.Add(_list);

        _btnVerify.Text = Loc.T("backup.verify");
        _btnVerify.Location = new Point(448, 24);
        _btnVerify.Size = new Size(124, 28);
        _btnVerify.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnVerify.Click += (_, _) => Verify();
        listBox.Controls.Add(_btnVerify);

        _btnDelete.Text = Loc.T("backup.delete");
        _btnDelete.Location = new Point(448, 58);
        _btnDelete.Size = new Size(124, 28);
        _btnDelete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnDelete.Click += (_, _) => DeleteSelected();
        listBox.Controls.Add(_btnDelete);

        _btnRefresh.Text = Loc.T("backup.refresh");
        _btnRefresh.Location = new Point(448, 92);
        _btnRefresh.Size = new Size(124, 28);
        _btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnRefresh.Click += (_, _) => Reload();
        listBox.Controls.Add(_btnRefresh);

        _lblLast.ForeColor = Faint;
        _lblLast.Location = new Point(16, 186);
        _lblLast.Size = new Size(584, 30);
        _lblLast.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        listBox.Controls.Add(_lblLast);

        // --- состояние ------------------------------------------------------
        _lblStatus.ForeColor = Muted;
        _lblStatus.Location = new Point(12, 572);
        _lblStatus.Size = new Size(616, 34);
        _lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_lblStatus);

        // Копия с движком делается минуты, и без движения человек не понимает, идёт работа
        // или панель встала. Полоса бежит всё время, пока копия собирается или проверяется.
        _bar.Location = new Point(12, 610);
        _bar.Size = new Size(616, 12);
        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _bar.Visible = false;
        Controls.Add(_bar);

        // --- кнопки окна ---------------------------------------------------
        // Ширины подобраны так, чтобы между кнопками был зазор: в прежней раскладке
        // «Восстановить из копии…» налезала на «Сохранить».
        _btnCreate.Text = Loc.T("backup.create");
        _btnCreate.Location = new Point(12, 628);
        _btnCreate.Size = new Size(200, 34);
        _btnCreate.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _btnCreate.Click += async (_, _) => await CreateAsync();
        Controls.Add(_btnCreate);

        _btnRestore.Text = Loc.T("backup.restore");
        _btnRestore.Location = new Point(224, 628);
        _btnRestore.Size = new Size(200, 34);
        _btnRestore.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _btnRestore.Click += (_, _) => OpenRestore();
        Controls.Add(_btnRestore);

        // Кнопка есть всегда, даже когда окно открыто для снимка или проверки вёрстки:
        // иначе проверка вёрстки не видит её и пропускает наложение на соседнюю кнопку.
        _btnRestore.Enabled = _onRestore != null;

        var save = new Button
        {
            Text = Loc.T("settings.save"),
            DialogResult = DialogResult.OK,
            Location = new Point(436, 628),
            Size = new Size(96, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        save.Click += (_, _) => ApplyTo(_settings);
        Controls.Add(save);

        var close = new Button
        {
            Text = Loc.T("settings.close"),
            DialogResult = DialogResult.Cancel,
            Location = new Point(540, 628),
            Size = new Size(88, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        Controls.Add(close);

        AcceptButton = save;
        CancelButton = close;

        WireTips(when, where, listBox, save, close);

        _chkAuto.CheckedChanged += (_, _) => SyncEnabled();
        _chkEngine.CheckedChanged += (_, _) => SyncEnabled();
    }

    /// <summary>
    /// Подсказки к элементам окна: по подписям не всегда понятно, что за что отвечает,
    /// а держать пояснения текстом в окне — значит сделать его ещё плотнее.
    /// </summary>
    private void WireTips(GroupBox when, GroupBox where, GroupBox listBox, Button save, Button close)
    {
        _tips.SetToolTip(when, Loc.T("backup.tip.when"));
        _tips.SetToolTip(_chkAuto, Loc.T("backup.tip.auto"));
        _tips.SetToolTip(_chkEngine, Loc.T("backup.tip.engine"));
        _tips.SetToolTip(_chkSessions, Loc.T("backup.tip.sessions"));
        _tips.SetToolTip(_numKeep, Loc.T("backup.tip.keep"));
        _tips.SetToolTip(where, Loc.T("backup.tip.folder"));
        _tips.SetToolTip(_txtFolder, Loc.T("backup.tip.folder"));
        _tips.SetToolTip(_chkKeys, Loc.T("backup.tip.keys"));
        _tips.SetToolTip(_btnAddKeyDir, Loc.T("backup.tip.keys"));
        _tips.SetToolTip(_lblKeysHint, Loc.T("backup.tip.keys"));
        _tips.SetToolTip(listBox, Loc.T("backup.tip.list"));
        _tips.SetToolTip(_btnVerify, Loc.T("backup.tip.check"));
        _tips.SetToolTip(_btnDelete, Loc.T("backup.tip.delete"));
        _tips.SetToolTip(_btnRefresh, Loc.T("backup.tip.refresh"));
        _tips.SetToolTip(_btnCreate, Loc.T("backup.tip.create"));
        _tips.SetToolTip(_btnRestore, Loc.T("backup.tip.restore"));
        _tips.SetToolTip(save, Loc.T("backup.tip.save"));
        _tips.SetToolTip(close, Loc.T("backup.tip.close"));
    }

    /// <summary>Открывает накат с выбранной копией и обновляет список: могла появиться предохранительная.</summary>
    private void OpenRestore()
    {
        _onRestore(Selected()?.Path);
        Reload();
    }

    // --- значения ----------------------------------------------------------

    private void LoadValues()
    {
        _chkAuto.Checked = _settings.BackupEnabled;
        _numHours.Value = Math.Clamp(_settings.BackupIntervalHours, 1, 720);
        _chkEngine.Checked = _settings.BackupWithEngine;
        _chkSessions.Checked = _settings.BackupWithSessions;
        _numKeep.Value = Math.Clamp(_settings.BackupKeepCount, 1, 100);
        _txtFolder.Text = _backups.Folder;
        _chkKeys.Checked = _settings.BackupWithKeys;
        SyncKeysHint();
        SyncEnabled();
        Reload();
    }

    private void SyncEnabled()
    {
        _numHours.Enabled = _chkAuto.Checked;
        _lblEvery.Enabled = _chkAuto.Checked;
        _lblHours.Enabled = _chkAuto.Checked;
        _chkSessions.Enabled = true;
    }

    public AppSettings ApplyTo(AppSettings settings)
    {
        settings.BackupEnabled = _chkAuto.Checked;
        settings.BackupIntervalHours = (int)_numHours.Value;
        settings.BackupWithEngine = _chkEngine.Checked;
        settings.BackupWithSessions = _chkSessions.Checked;
        settings.BackupKeepCount = (int)_numKeep.Value;

        var folder = _txtFolder.Text.Trim();
        settings.BackupFolder = string.Equals(folder, BackupService.DefaultFolder(), StringComparison.OrdinalIgnoreCase)
            ? ""
            : folder;

        settings.BackupWithKeys = _chkKeys.Checked;
        return settings;
    }

    /// <summary>
    /// Объясняет, что именно попадёт в архив при включённой упаковке ключей. Пока ключи
    /// выключены, список показывается как «что будет, если включить» — чтобы решение было
    /// осознанным, а не наугад.
    /// </summary>
    /// <summary>
    /// Добавляет каталог ключей в список упаковываемых. Убрать каталог из списка можно
    /// в settings.json (backupKeyDirs) — в окне для этого места нет намеренно: случайно
    /// выкинуть ключ из копии хуже, чем не добавить его.
    /// </summary>
    private void AddKeyDirectory()
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Loc.T("backup.pickKeysDesc"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var directories = _settings.BackupKeyDirs ?? new List<string>();
            if (!directories.Any(existing => string.Equals(existing, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                directories.Add(dialog.SelectedPath);
            }

            _settings.BackupKeyDirs = directories;
            _settings.BackupWithKeys = true;
            _chkKeys.Checked = true;
            _settings.Save(_paths.SettingsPath);
            SyncKeysHint();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Loc.T("backup.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SyncKeysHint()
    {
        try
        {
            var directories = _backups.KeyDirectories().ToList();
            if (directories.Count == 0)
            {
                var shown = AppPaths.Display(AppPaths.SshDir);
                _lblKeysHint.Text = TextFit.Fit(_lblKeysHint, Loc.T("backup.keysNone", shown), shown);
                return;
            }

            var listed = directories.Select(AppPaths.Display).ToList();
            var text = Loc.T(_chkKeys.Checked ? "backup.keysOn" : "backup.keysIfOn", string.Join("; ", listed));
            _lblKeysHint.Text = TextFit.Fit(_lblKeysHint, text, listed.ToArray());
        }
        catch (Exception error)
        {
            _lblKeysHint.Text = error.Message;
        }
    }

    private void Reload()
    {
        var entries = _backups.List();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(entry.Describe());
        _list.EndUpdate();

        if (entries.Count > 0)
        {
            _list.SelectedIndex = 0;
            _lblLast.Text = Loc.T("backup.last", entries[0].Describe());
            if (!entries[0].Readable)
            {
                _lblLast.ForeColor = AmberText;
                _lblLast.Text += Loc.T("backup.incomplete");
            }
            else
            {
                _lblLast.ForeColor = Faint;
            }
        }
        else
        {
            _lblLast.ForeColor = Faint;
            _lblLast.Text = Loc.T("backup.none");
        }
    }

    private BackupEntry Selected()
    {
        var entries = _backups.List();
        var index = _list.SelectedIndex;
        return index >= 0 && index < entries.Count ? entries[index] : null;
    }

    // --- действия ----------------------------------------------------------

    private async Task CreateAsync()
    {
        if (_busy) return;

        ApplyTo(_settings);
        _settings.Save(_paths.SettingsPath);

        _busy = true;
        _btnCreate.Enabled = false;
        _bar.Visible = true;
        _lblStatus.ForeColor = Muted;
        _lblStatus.Text = Loc.T("backup.creating");
        Refresh();

        var service = new BackupService(_paths, _settings);
        var lastStep = "";

        var result = await Task.Run(() => service.Create(step => lastStep = step));

        _busy = false;
        _btnCreate.Enabled = true;
        _bar.Visible = false;

        AppLog.Write(_paths, "резервная копия (по кнопке): " + result.Summary());

        _lblStatus.ForeColor = result.Ok ? GreenText : AmberText;
        var status = result.Summary()
                     + (result.Ok ? Environment.NewLine + Loc.T("backup.file", result.Path) : "");
        if (!result.Ok && result.Error.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            status += Environment.NewLine + Loc.T("backup.noWriteAccess");
        }

        // Путь к архиву длинный, а метка узкая — укорачиваем по середине, если не влезает.
        _lblStatus.Text = TextFit.Fit(_lblStatus, status, result.Path);

        if (!string.IsNullOrEmpty(lastStep)) _lblLast.Text = Loc.T("backup.lastStep", lastStep);
        _txtFolder.Text = _backups.Folder;
        Reload();
    }

    private void Verify()
    {
        var entry = Selected();
        if (entry == null)
        {
            _lblStatus.ForeColor = Muted;
            _lblStatus.Text = Loc.T("backup.selectCopy");
            return;
        }

        _lblStatus.ForeColor = Muted;
        _lblStatus.Text = Loc.T("backup.checking", entry.Name);
        Refresh();

        var check = BackupService.Verify(entry.Path);
        _lblStatus.ForeColor = check.Ok ? GreenText : AmberText;
        _lblStatus.Text = check.Summary() + Environment.NewLine + entry.Name;
        AppLog.Write(_paths, "проверка копии " + entry.Name + ": " + check.Summary());
    }

    private void DeleteSelected()
    {
        var entry = Selected();
        if (entry == null)
        {
            _lblStatus.ForeColor = Muted;
            _lblStatus.Text = Loc.T("backup.selectCopy");
            return;
        }

        var answer = MessageBox.Show(this,
            Loc.T("backup.deleteAsk", entry.Name) + Environment.NewLine + entry.Describe(),
            Loc.T("backup.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        try
        {
            File.Delete(entry.Path);
            AppLog.Write(_paths, "копия удалена: " + entry.Name);
            _lblStatus.ForeColor = Muted;
            _lblStatus.Text = Loc.T("backup.deleted", entry.Name);
        }
        catch (Exception error)
        {
            _lblStatus.ForeColor = AmberText;
            _lblStatus.Text = Loc.T("backup.deleteFailed", error.Message);
        }

        Reload();
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("backup.pickFolderDesc"),
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_txtFolder.Text) ? _txtFolder.Text : BackupService.DefaultFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _txtFolder.Text = dialog.SelectedPath;
        ApplyTo(_settings);
        _settings.Save(_paths.SettingsPath);
        Reload();
    }

    private void OpenFolder()
    {
        try
        {
            var folder = _backups.Folder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Loc.T("backup.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
