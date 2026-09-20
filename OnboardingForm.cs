using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Мастер первой настройки. Показывается один раз при первом запуске (флаг onboarded
/// в настройках), заново вызывается ключом --onboard.
///
/// Шаги: язык интерфейса; проверка окружения (Node и пакет dsh, с полями путей, если
/// панель не нашла их сама); рабочая папка — каталог, из которого запускается DSH —
/// и порт сервера; резервные копии; ключи и сертификаты.
///
/// Ключи по умолчанию **не** упаковываются: это приватные файлы, и архив с ними
/// становится секретом. Вопрос задаётся именно здесь, чтобы человек решил это сам.
/// </summary>
public sealed class OnboardingForm : Form
{
    private const int TotalSteps = 5;

    /// <summary>Сколько шагов у мастера — нужно проверке вёрстки, чтобы обойти их все.</summary>
    internal const int StepCount = TotalSteps;
    private static readonly string[] LanguageCodes = { "auto", "ru", "en", "zh" };

    private static readonly Color Muted = Color.FromArgb(110, 110, 110);
    private static readonly Color Faint = Color.FromArgb(130, 130, 130);
    private static readonly Color GreenText = Color.FromArgb(21, 128, 61);
    private static readonly Color AmberText = Color.FromArgb(161, 98, 7);

    /// <summary>Неудача (скачать не удалось, установка не прошла) — красным, чтобы отличать
    /// от «пока не настроено», которое янтарное.</summary>
    private static readonly Color RedText = Color.FromArgb(185, 28, 28);

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly Action<string> _onLanguageChanged;

    private readonly Label _caption = new();
    private readonly Panel _content = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly Button _skip = new();
    private readonly Panel[] _pages = new Panel[TotalSteps];

    // шаг 1 — язык
    private readonly ComboBox _cmbLanguage = new();
    private readonly Label _lblLanguageHint = new();

    // шаг 2 — окружение
    private readonly Label _lblNodeState = new();
    private readonly Label _lblDshState = new();
    private readonly Label _lblNodeCaption = new();
    private readonly Label _lblDshCaption = new();
    private readonly TextBox _txtNode = new();
    private readonly TextBox _txtDsh = new();
    private readonly Button _btnRecheck = new();
    private readonly Button _btnInstallNode = new();
    private readonly Button _btnInstallEngine = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _lblEnvHint = new();

    // шаг 3 — рабочая папка и порт
    private readonly Label _lblDirCaption = new();
    private readonly TextBox _txtDir = new();
    private readonly Button _btnPickDir = new();
    private readonly Label _lblDirHint = new();
    private readonly Label _lblPortCaption = new();
    private readonly NumericUpDown _numPort = new();
    private readonly Label _lblPortState = new();

    // шаг 4 — резервные копии
    private readonly CheckBox _chkBackup = new();
    private readonly Label _lblBackupFolder = new();
    private readonly TextBox _txtBackupFolder = new();
    private readonly Button _btnPickBackup = new();
    private readonly Label _lblInterval = new();
    private readonly NumericUpDown _numInterval = new();
    private readonly Label _lblKeep = new();
    private readonly NumericUpDown _numKeep = new();

    // шаг 5 — ключи
    private readonly CheckBox _chkKeys = new();
    private readonly Label _lblKeysHint = new();

    private int _step;
    private bool _suppressLanguage;

    /// <summary>В настройках папка копий была пустой — в поле показано значение по умолчанию.</summary>
    private bool _autoBackupFolder;

    /// <summary>Идёт скачивание или установка: кнопки на это время выключаем.</summary>
    private bool _busy;

    /// <summary>Найден ли Node и движок: от этого зависит, активны ли кнопки установки.</summary>
    private bool _nodeFound;
    private bool _dshFound;

    public OnboardingForm(AppPaths paths, AppSettings settings, Action<string> onLanguageChanged = null)
    {
        _paths = paths;
        _settings = settings;
        _onLanguageChanged = onLanguageChanged;

        Text = Loc.T("onboard.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(576, 420);
        Font = Loc.UiFont(9F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        LoadValues();
        ApplyTexts();
        ShowStep(0);
    }

    /// <summary>Пользователь прошёл (или пропустил) мастер — настройки сохранены.</summary>
    public bool Completed { get; private set; }

    private void BuildLayout()
    {
        _caption.Location = new Point(16, 14);
        _caption.Size = new Size(544, 22);
        _caption.Font = Loc.UiFont(11F, FontStyle.Bold);
        _caption.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_caption);

        _content.Location = new Point(12, 44);
        _content.Size = new Size(552, 320);
        _content.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _content.BorderStyle = BorderStyle.FixedSingle;
        _content.BackColor = Color.White;
        Controls.Add(_content);

        for (var index = 0; index < TotalSteps; index++)
        {
            _pages[index] = new Panel { Dock = DockStyle.Fill, Visible = false };
            _content.Controls.Add(_pages[index]);
        }

        BuildLanguagePage();
        BuildEnvironmentPage();
        BuildFolderPage();
        BuildBackupPage();
        BuildKeysPage();

        _skip.Location = new Point(12, 376);
        _skip.Size = new Size(120, 30);
        _skip.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _skip.Click += (_, _) => Finish(skipped: true);
        Controls.Add(_skip);

        _back.Location = new Point(330, 376);
        _back.Size = new Size(110, 30);
        _back.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _back.Click += (_, _) => ShowStep(_step - 1);
        Controls.Add(_back);

        _next.Location = new Point(450, 376);
        _next.Size = new Size(114, 30);
        _next.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _next.Click += (_, _) => Next();
        Controls.Add(_next);

        AcceptButton = _next;
    }

    private void BuildLanguagePage()
    {
        var page = _pages[0];

        _cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbLanguage.Location = new Point(16, 24);
        _cmbLanguage.Size = new Size(240, 24);
        _cmbLanguage.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressLanguage) return;
            var code = LanguageCodes[Math.Max(0, _cmbLanguage.SelectedIndex)];
            _onLanguageChanged?.Invoke(code);
            _settings.Language = code;
            ApplyTexts();
        };
        page.Controls.Add(_cmbLanguage);

        _lblLanguageHint.ForeColor = Faint;
        _lblLanguageHint.Location = new Point(16, 58);
        _lblLanguageHint.Size = new Size(500, 40);
        page.Controls.Add(_lblLanguageHint);
    }

    private void BuildEnvironmentPage()
    {
        var page = _pages[1];

        _lblNodeState.Location = new Point(16, 14);
        _lblNodeState.Size = new Size(528, 34);
        page.Controls.Add(_lblNodeState);

        _lblDshState.Location = new Point(16, 52);
        _lblDshState.Size = new Size(528, 34);
        page.Controls.Add(_lblDshState);

        _lblNodeCaption.Location = new Point(16, 98);
        _lblNodeCaption.Size = new Size(150, 20);
        page.Controls.Add(_lblNodeCaption);

        _txtNode.Location = new Point(170, 94);
        _txtNode.Size = new Size(360, 24);
        page.Controls.Add(_txtNode);

        _lblDshCaption.Location = new Point(16, 130);
        _lblDshCaption.Size = new Size(150, 20);
        page.Controls.Add(_lblDshCaption);

        _txtDsh.Location = new Point(170, 126);
        _txtDsh.Size = new Size(360, 24);
        page.Controls.Add(_txtDsh);

        _btnRecheck.Location = new Point(16, 164);
        _btnRecheck.Size = new Size(150, 30);
        _btnRecheck.Click += (_, _) => CheckEnvironment();
        page.Controls.Add(_btnRecheck);

        // Дальше — то, ради чего человек здесь оказался впервые: поставить сам агент.
        // Команду npm можно набрать руками, но новичку это тяжело: PowerShell не даёт
        // вставить строку с кавычками, а политика выполнения скриптов запрещает .ps1.
        // Поэтому панель запускает установщик Node и npm за человека.
        _btnInstallNode.Text = Loc.T("onboard.installNode");
        _btnInstallNode.Location = new Point(172, 164);
        _btnInstallNode.Size = new Size(180, 30);
        _btnInstallNode.Click += async (_, _) => await InstallNodeAsync();
        page.Controls.Add(_btnInstallNode);

        _btnInstallEngine.Text = Loc.T("onboard.installEngine");
        _btnInstallEngine.Location = new Point(358, 164);
        _btnInstallEngine.Size = new Size(178, 30);
        _btnInstallEngine.Click += async (_, _) => await InstallEngineAsync();
        page.Controls.Add(_btnInstallEngine);

        // Прогресс: установка Node и движка длится минуты, и без движения человек думает,
        // что панель зависла. Полоса бежит, пока идёт работа.
        _bar.Location = new Point(16, 196);
        _bar.Size = new Size(520, 12);
        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.Visible = false;
        page.Controls.Add(_bar);

        _lblEnvHint.ForeColor = Faint;
        _lblEnvHint.Location = new Point(16, 214);
        _lblEnvHint.Size = new Size(520, 54);
        page.Controls.Add(_lblEnvHint);
    }

    private void BuildFolderPage()
    {
        var page = _pages[2];

        _lblDirCaption.Location = new Point(16, 16);
        _lblDirCaption.Size = new Size(180, 20);
        page.Controls.Add(_lblDirCaption);

        _txtDir.Location = new Point(200, 12);
        _txtDir.Size = new Size(250, 24);
        page.Controls.Add(_txtDir);

        _btnPickDir.Location = new Point(456, 11);
        _btnPickDir.Size = new Size(86, 26);
        _btnPickDir.Click += (_, _) => PickFolder(_txtDir, Loc.T("onboard.dir.title"));
        page.Controls.Add(_btnPickDir);

        _lblDirHint.ForeColor = Faint;
        _lblDirHint.Location = new Point(16, 44);
        _lblDirHint.Size = new Size(510, 40);
        page.Controls.Add(_lblDirHint);

        _lblPortCaption.Location = new Point(16, 104);
        _lblPortCaption.Size = new Size(120, 20);
        page.Controls.Add(_lblPortCaption);

        _numPort.Minimum = 1;
        _numPort.Maximum = 65535;
        _numPort.Value = 3080;
        _numPort.Location = new Point(140, 100);
        _numPort.Size = new Size(90, 24);
        _numPort.ValueChanged += (_, _) => CheckPort();
        page.Controls.Add(_numPort);

        // Две строки: сообщение о том, что порт держит наша же панель, длиннее одной строки.
        _lblPortState.Location = new Point(250, 100);
        _lblPortState.Size = new Size(280, 34);
        page.Controls.Add(_lblPortState);
    }

    private void BuildBackupPage()
    {
        var page = _pages[3];

        _chkBackup.Location = new Point(16, 14);
        _chkBackup.Size = new Size(510, 22);
        page.Controls.Add(_chkBackup);

        _lblBackupFolder.Location = new Point(16, 54);
        _lblBackupFolder.Size = new Size(130, 20);
        page.Controls.Add(_lblBackupFolder);

        _txtBackupFolder.Location = new Point(150, 50);
        _txtBackupFolder.Size = new Size(288, 24);
        page.Controls.Add(_txtBackupFolder);

        _btnPickBackup.Location = new Point(446, 49);
        _btnPickBackup.Size = new Size(96, 26);
        _btnPickBackup.Click += (_, _) => PickFolder(_txtBackupFolder, Loc.T("onboard.backup.folder"));
        page.Controls.Add(_btnPickBackup);

        _lblInterval.Location = new Point(16, 96);
        _lblInterval.Size = new Size(130, 20);
        page.Controls.Add(_lblInterval);

        _numInterval.Minimum = 1;
        _numInterval.Maximum = 720;
        _numInterval.Value = 24;
        _numInterval.Location = new Point(150, 92);
        _numInterval.Size = new Size(80, 24);
        page.Controls.Add(_numInterval);

        _lblKeep.Location = new Point(16, 132);
        _lblKeep.Size = new Size(130, 20);
        page.Controls.Add(_lblKeep);

        _numKeep.Minimum = 1;
        _numKeep.Maximum = 100;
        _numKeep.Value = 5;
        _numKeep.Location = new Point(150, 128);
        _numKeep.Size = new Size(80, 24);
        page.Controls.Add(_numKeep);
    }

    private void BuildKeysPage()
    {
        var page = _pages[4];

        _chkKeys.Location = new Point(16, 20);
        _chkKeys.Size = new Size(510, 22);
        page.Controls.Add(_chkKeys);

        _lblKeysHint.ForeColor = AmberText;
        _lblKeysHint.Location = new Point(16, 56);
        _lblKeysHint.Size = new Size(510, 90);
        page.Controls.Add(_lblKeysHint);
    }

    /// <summary>Подписи собираются здесь, чтобы их можно было пересобрать после смены языка.</summary>
    private void ApplyTexts()
    {
        Text = Loc.T("onboard.title");
        Font = Loc.UiFont(9F);
        _caption.Font = Loc.UiFont(11F, FontStyle.Bold);

        // Подпись шага: без неё при смене языка на первом шаге строка «Шаг 1 из 5 · …»
        // оставалась на прежнем языке до перехода на другой шаг.
        _caption.Text = Loc.T("onboard.step", _step + 1, TotalSteps) + " · " + Loc.T(StepTitleKey(_step));

        _skip.Text = Loc.T("onboard.skip");
        _back.Text = Loc.T("onboard.back");
        _next.Text = _step >= TotalSteps - 1 ? Loc.T("onboard.finish") : Loc.T("onboard.next");

        _lblLanguageHint.Text = Loc.T("settings.language.applies");

        _lblNodeCaption.Text = Loc.T("onboard.env.nodePath");
        _lblDshCaption.Text = Loc.T("onboard.env.dshPath");
        _btnRecheck.Text = Loc.T("onboard.env.recheck");
        _lblEnvHint.Text = Loc.T("onboard.env.hint");

        _lblDirCaption.Text = Loc.T("onboard.dir.title");
        _btnPickDir.Text = Loc.T("onboard.dir.pick");
        _lblDirHint.Text = Loc.T("onboard.dir.hint");
        _lblPortCaption.Text = Loc.T("onboard.port.title");

        _chkBackup.Text = Loc.T("onboard.backup.enable");
        _lblBackupFolder.Text = Loc.T("onboard.backup.folder");
        _btnPickBackup.Text = Loc.T("onboard.dir.pick");
        _lblInterval.Text = Loc.T("onboard.backup.interval");
        _lblKeep.Text = Loc.T("onboard.backup.keep");

        _chkKeys.Text = Loc.T("onboard.keys.enable");
        _lblKeysHint.Text = Loc.T("onboard.keys.hint", Loc.T("onboard.keys.ssh"));

        FillLanguages();
        CheckEnvironment();
        CheckPort();
    }

    private void FillLanguages()
    {
        _suppressLanguage = true;
        try
        {
            _cmbLanguage.Items.Clear();
            _cmbLanguage.Items.Add(Loc.T("settings.language.auto"));
            _cmbLanguage.Items.Add("Русский");
            _cmbLanguage.Items.Add("English");
            _cmbLanguage.Items.Add("中文");

            var index = Array.IndexOf(LanguageCodes, (_settings.Language ?? "auto").Trim().ToLowerInvariant());
            _cmbLanguage.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressLanguage = false;
        }
    }

    private void LoadValues()
    {
        _txtNode.Text = _settings.NodePath ?? "";
        _txtDsh.Text = _settings.DshBinPath ?? "";
        _txtDir.Text = _settings.ServerWorkingDir ?? "";
        _numPort.Value = Math.Clamp(_settings.ServerPort, 1, 65535);
        _chkBackup.Checked = _settings.BackupEnabled;

        // Пустое поле выглядело как незаполненное. Показываем папку, в которую копии и так
        // лягут, но помечаем её как значение по умолчанию: если человек её не тронул,
        // в настройки вернётся пустое значение, и путь пересчитается при следующем запуске.
        _autoBackupFolder = string.IsNullOrWhiteSpace(_settings.BackupFolder);
        _txtBackupFolder.Text = _autoBackupFolder
            ? BackupService.DefaultFolder()
            : _settings.BackupFolder.Trim();
        _numInterval.Value = Math.Clamp(_settings.BackupIntervalHours, 1, 720);
        _numKeep.Value = Math.Clamp(_settings.BackupKeepCount, 1, 100);
        _chkKeys.Checked = _settings.BackupWithKeys;
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, TotalSteps - 1);
        for (var index = 0; index < TotalSteps; index++) _pages[index].Visible = index == _step;

        _caption.Text = Loc.T("onboard.step", _step + 1, TotalSteps) + " · " + Loc.T(StepTitleKey(_step));
        _back.Enabled = _step > 0;
        _next.Text = Loc.T(_step >= TotalSteps - 1 ? "onboard.finish" : "onboard.next");

        if (_step == 1) CheckEnvironment();
        if (_step == 2) CheckPort();
    }

    /// <summary>
    /// Показать шаг для проверки вёрстки: у непоказанных шагов панели не разложены, поэтому
    /// проверять их ширину и координаты нельзя, пока шаг не открыт.
    /// </summary>
    internal void ShowStepForCheck(int step) => ShowStep(step);

    /// <summary>Сообщение под кнопками: что сейчас делается или что получилось.</summary>
    private void SetHint(string text, Color color)
    {
        _lblEnvHint.ForeColor = color;
        _lblEnvHint.Text = text;
    }

    private void Busy(bool busy, string hint)
    {
        _busy = busy;
        _btnRecheck.Enabled = !busy;
        ApplyInstallButtons();

        // Пока идёт установка, закрывать мастер нельзя: работа оборвалась бы на середине,
        // а обратный вызов прогресса прилетел бы в уничтоженное окно.
        _next.Enabled = !busy;
        _skip.Enabled = !busy;
        _back.Enabled = !busy && _step > 0;

        _bar.Visible = busy;
        UseWaitCursor = busy;
        if (hint != null) SetHint(hint, Muted);
    }

    /// <summary>
    /// Кнопки установки включаем только для того, чего действительно нет: если Node или движок
    /// уже найдены, кнопка гаснет — иначе человек жмёт «Установить» второй раз, хотя ставить
    /// нечего (владелец просил убрать эту возможность).
    /// </summary>
    private void ApplyInstallButtons()
    {
        if (_busy) return;

        _btnInstallNode.Enabled = !_nodeFound;
        _btnInstallEngine.Enabled = !_dshFound;
    }

    /// <summary>
    /// Обновляет подпись из фоновой задачи. Окно к этому моменту могло закрыться, поэтому
    /// проверяем его состояние: BeginInvoke на уничтоженном окне бросает исключение.
    /// </summary>
    private void SafeUi(string text)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed) SetHint(text, Muted);
            }));
        }
        catch (InvalidOperationException)
        {
            // Окно закрылось между проверкой и вызовом — обновлять уже нечего.
        }
    }


    /// <summary>
    /// Ставит Node за человека: панель находит свежий LTS и ставит его — машинно, если есть
    /// права, иначе распаковывает переносимую сборку к себе. Дальше человек возвращается сюда
    /// и жмёт «Проверить снова».
    /// </summary>
    private async Task InstallNodeAsync()
    {
        if (_busy) return;

        var question = Loc.T("onboard.installNodeAsk");
        if (MessageBox.Show(this, question, Loc.T("onboard.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        Busy(true, Loc.T("onboard.nodeLooking"));
        try
        {
            var result = await Task.Run(() => NodeInstaller.Install(_paths, _settings,
                step => SafeUi(step)));

            AppLog.Write(_paths, "Node: " + result.Summary());
            if (result.Ok)
            {
                RememberPaths();
                SetHint(result.Summary(), GreenText);
                CheckEnvironment();
            }
            else
            {
                SetHint(Loc.T("onboard.nodeDownloadFailed", result.Error), RedText);
            }
        }
        finally
        {
            Busy(false, null);
        }
    }

    /// <summary>
    /// Ставит движок DSH сам: запускает npm install -g @deepseek-ai/dsh и показывает хвост
    /// вывода. Так человеку не нужно ни разрешать выполнение скриптов, ни копировать команду
    /// в PowerShell (а выделить её из подписи и вставить туда всё равно нечем).
    /// </summary>
    private async Task InstallEngineAsync()
    {
        if (_busy) return;

        var npm = FindNpm();
        if (npm.Length == 0)
        {
            SetHint(Loc.T("onboard.engineNoNpm"), RedText);
            return;
        }

        Busy(true, Loc.T("onboard.engineRunning"));
        try
        {
            var result = await Task.Run(() => RunNpm(npm));
            if (result.Ok)
            {
                // Пути записываем сразу: движок лёг в наш же переносимый Node, и панель должна
                // знать это и в полях, и в настройках — иначе она не находит то, что сама поставила.
                RememberPaths();
                SetHint(Loc.T("onboard.engineDone"), GreenText);
                CheckEnvironment();
            }
            else
            {
                SetHint(Loc.T("onboard.engineFailed", result.Tail), RedText);
            }
        }
        finally
        {
            Busy(false, null);
        }
    }


    /// <summary>
    /// Запоминает найденные пути: и в поля (человек должен видеть, что нашлось), и в настройки
    /// (панель будет искать движок по ним, даже если список обычных мест изменится).
    ///
    /// Зачем: после установки движка «в себя» поля оставались пустыми, а строка состояния
    /// продолжала говорить «пакет dsh не найден» — панель не узнавала то, что сама поставила.
    /// Заполняем только пустые поля: то, что человек ввёл руками, не трогаем.
    /// </summary>
    private void RememberPaths()
    {
        try
        {
            var node = NodeLocator.TryResolveNode();
            if (node.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(_txtNode.Text)) _txtNode.Text = node;
                _settings.NodePath = node;
            }

            var binPath = "";
            try { binPath = NodeLocator.ResolveDshBin(); } catch { }
            if (binPath.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(_txtDsh.Text)) _txtDsh.Text = binPath;
                _settings.DshBinPath = binPath;
            }

            if (node.Length > 0 || binPath.Length > 0) _settings.Save(_paths.SettingsPath);
        }
        catch
        {
            // Не вышло — мастер покажет то, что найдётся при проверке окружения.
        }
    }

    /// <summary>npm.cmd: рядом с node.exe, в папке глобальных пакетов или в PATH.</summary>
    private static string FindNpm()
    {
        var candidates = new List<string>();
        try
        {
            var nodeDir = Path.GetDirectoryName(NodeLocator.ResolveNode() ?? "");
            if (!string.IsNullOrEmpty(nodeDir)) candidates.Add(Path.Combine(nodeDir, "npm.cmd"));
        }
        catch
        {
            // Node ещё не найден — проверим обычные места ниже.
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            var info = new ProcessStartInfo("where.exe", "npm.cmd")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(info);
            if (process != null)
            {
                var line = process.StandardOutput.ReadLine();
                process.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim())) return line.Trim();
            }
        }
        catch
        {
            // Искать больше негде.
        }

        return "";
    }

    /// <summary>Установка движка: длится минуты, поэтому на фоне, а человеку — причина или итог.</summary>
    private (bool Ok, string Tail) RunNpm(string npm)
    {
        try
        {
            // Именно cmd: npm.cmd — пакетный файл, напрямую его не запустить.
            // ProcessRunner читает оба потока сразу: иначе большой вывод npm в stderr
            // подвешивал установку навсегда.
            var outcome = ProcessRunner.Run("cmd.exe", "/c \"\"" + npm + "\" install -g @deepseek-ai/dsh\"",
                600000, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (!outcome.Started) return (false, outcome.Error);

            // Полный вывод — в журнал панели: по нему разбираются, почему npm не смог.
            AppLog.Write(_paths, "npm install -g @deepseek-ai/dsh (код " + outcome.ExitCode + "):" +
                              Environment.NewLine + outcome.Output);

            var lines = outcome.Output.Replace("\r", "").Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            // Сначала ищем строки с ошибкой: хвост вывода — это обычно уведомления npm
            // («To update run…»), по ним причину не видно.
            var errors = lines.Where(line => line.Contains("npm error", StringComparison.OrdinalIgnoreCase)
                                             || line.Contains("ERR!", StringComparison.OrdinalIgnoreCase)).ToList();
            var tail = errors.Count > 0 ? string.Join(" · ", errors.TakeLast(3)) : string.Join(" · ", lines.TakeLast(3));

            if (outcome.TimedOut) return (false, Loc.T("onboard.engineTimeout"));
            return (outcome.ExitCode == 0, tail.Length > 300 ? tail[^300..] : tail);
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }

    private static string StepTitleKey(int step) => step switch
    {
        0 => "onboard.step1.title",
        1 => "onboard.step2.title",
        2 => "onboard.step3.title",
        3 => "onboard.step4.title",
        _ => "onboard.step5.title",
    };

    private void Next()
    {
        if (_step == 1) NodeLocator.ApplyOverrides(_txtNode.Text, _txtDsh.Text);
        if (_step >= TotalSteps - 1) Finish(skipped: false);
        else ShowStep(_step + 1);
    }

    private void CheckEnvironment()
    {
        NodeLocator.ApplyOverrides(_txtNode.Text, _txtDsh.Text);

        try
        {
            // Показываем настоящий путь, а не %USERPROFILE%: подстановка выглядит как
            // незаполненная переменная, и человек не понимает, что нашлось.
            var node = NodeLocator.ResolveNode();
            _nodeFound = true;
            _lblNodeState.ForeColor = GreenText;
            _lblNodeState.Text = "✓ " + TextFit.Fit(_lblNodeState, Loc.T("onboard.env.nodeFound", node), node);
        }
        catch
        {
            _nodeFound = false;
            _lblNodeState.ForeColor = AmberText;
            _lblNodeState.Text = "• " + Loc.T("onboard.env.nodeMissing");
        }

        try
        {
            var bin = NodeLocator.ResolveDshBin();
            _dshFound = true;
            _lblDshState.ForeColor = GreenText;
            _lblDshState.Text = "✓ " + TextFit.Fit(_lblDshState, Loc.T("onboard.env.dshFound", bin), bin);
        }
        catch
        {
            _dshFound = false;
            _lblDshState.ForeColor = AmberText;
            _lblDshState.Text = "• " + Loc.T("onboard.env.dshMissing");
        }

        // Кнопки установки гаснут для того, что уже найдено: иначе человек жмёт «Установить»
        // второй раз, хотя ставить нечего.
        ApplyInstallButtons();
    }

    private void CheckPort()
    {
        var port = (int)_numPort.Value;
        var (owner, name, ours) = ServerController.Listener(port, _paths);

        if (ours)
        {
            // Порт держит наша же панель — это не помеха, а признак, что всё уже работает.
            _lblPortState.ForeColor = GreenText;
            _lblPortState.Text = Loc.T("onboard.port.ours", owner);
        }
        else if (owner > 0)
        {
            _lblPortState.ForeColor = AmberText;
            _lblPortState.Text = Loc.T("onboard.port.busy", name, owner);
        }
        else
        {
            _lblPortState.ForeColor = GreenText;
            _lblPortState.Text = Loc.T("onboard.port.free");
        }
    }

    private void PickFolder(TextBox target, string title)
    {
        try
        {
            using var dialog = new FolderBrowserDialog { Description = title, ShowNewFolderButton = true };
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Loc.T("onboard.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Сохраняет ответы. «Пропустить» тоже отмечает мастер пройденным, чтобы он не всплывал каждый запуск.</summary>
    private void Finish(bool skipped)
    {
        try
        {
            _settings.Language = LanguageCodes[Math.Max(0, _cmbLanguage.SelectedIndex)];
            if (!skipped)
            {
                _settings.NodePath = _txtNode.Text.Trim();
                _settings.DshBinPath = _txtDsh.Text.Trim();
                _settings.ServerWorkingDir = _txtDir.Text.Trim();
                _settings.ServerPort = (int)_numPort.Value;
                _settings.BackupEnabled = _chkBackup.Checked;

                // Папку по умолчанию в настройки не записываем: путь по умолчанию считается
                // от «Документов» при каждом запуске и переживёт их перенос.
                var folder = _txtBackupFolder.Text.Trim();
                _settings.BackupFolder = _autoBackupFolder && folder == BackupService.DefaultFolder() ? "" : folder;
                _settings.BackupIntervalHours = (int)_numInterval.Value;
                _settings.BackupKeepCount = (int)_numKeep.Value;
                _settings.BackupWithKeys = _chkKeys.Checked;
            }

            _settings.Onboarded = true;
            _settings.Save(_paths.SettingsPath);
            NodeLocator.ApplyOverrides(_settings.NodePath, _settings.DshBinPath);
            Completed = true;
            DialogResult = DialogResult.OK;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Loc.T("onboard.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Close();
    }
}
