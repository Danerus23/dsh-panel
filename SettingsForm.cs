using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Настройки панели: три вкладки.
///   «Общие» — язык, рабочая папка (каталог, из которого запускается DSH), порт сервера
///   и пути к node.exe и lib\bin.js, если панель не нашла их сама;
///   «Баланс и тариф» — автообновление баланса, предупреждение о низком балансе,
///   окна пика, ручная и автоматическая проверка страницы цен, справка по ценам;
///   «О панели» — имя, версия, лицензия.
/// Новые окна пика применяются только по кнопке. Подписи берутся из словарей (Loc),
/// поэтому окно умеет пересобираться (Retext) после смены языка, не теряя уже
/// выставленные значения.
/// </summary>
public sealed class SettingsForm : Form
{
    /// <summary>Коды языков в том же порядке, что и пункты списка.</summary>
    private static readonly string[] LanguageCodes = { "auto", "ru", "en", "zh" };

    /// <summary>Режимы темы в том же порядке, что и пункты списка (три положения).</summary>
    private static readonly string[] ThemeCodes = { "auto", "light", "dark" };

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly PricingUpdateService _pricing;
    private readonly Func<string> _currentWindows;
    private readonly Action<string> _onLanguageChanged;

    /// <summary>Смена темы: панель применяет её к своим окнам и сохраняет настройку.</summary>
    private readonly Action<string> _onThemeChanged;

    /// <summary>Что сделать, когда обновление подготовлено: панель запустит сценарий и закроется.</summary>
    private readonly Action<UpdateDownload> _onUpdateReady;

    private readonly TabControl _tabs = new();
    private readonly TabPage _pageGeneral = new();
    private readonly TabPage _pageBalance = new();
    private readonly TabPage _pageAbout = new();
    private readonly TabPage _pageUpdate = new();

    // --- ссылки и обновления ------------------------------------------------
    private readonly LinkLabel _lnkAboutRepo = new();
    private readonly LinkLabel _lnkAboutDonate = new();
    private readonly Label _lblUpdateCurrent = new();
    private readonly Label _lblUpdateGithub = new();
    private readonly Label _lblUpdateChecked = new();
    private readonly Button _btnUpdateCheck = new();
    private readonly Button _btnUpdateInstall = new();
    private readonly Button _btnUpdatePage = new();
    private readonly Label _lblUpdateNotes = new();
    private readonly TextBox _txtUpdateNotes = new();
    private readonly Label _lblUpdateNotesHint = new();
    private readonly ProgressBar _barUpdate = new();
    private readonly Label _lblUpdateState = new();

    /// <summary>Результат последней проверки — из него берём адреса сборок для скачивания.</summary>
    private UpdateCheck _lastUpdate;

    /// <summary>Идёт проверка или скачивание: кнопки на это время выключаем.</summary>
    private bool _updating;

    // --- общие -------------------------------------------------------------
    private readonly Label _lblLanguage = new();
    private readonly ComboBox _cmbLanguage = new();
    private readonly Label _lblLanguageHint = new();
    private readonly Label _lblTheme = new();
    private readonly ComboBox _cmbTheme = new();
    private readonly Label _lblWorkDir = new();
    private readonly TextBox _txtWorkDir = new();
    private readonly Button _btnWorkDir = new();
    private readonly Label _lblWorkDirHint = new();
    private readonly Label _lblPort = new();
    private readonly NumericUpDown _numPort = new();
    private readonly Label _lblPortState = new();
    private readonly Label _lblNodePath = new();
    private readonly TextBox _txtNodePath = new();
    private readonly Label _lblDshPath = new();
    private readonly TextBox _txtDshPath = new();
    private readonly Label _lblEnvHint = new();
    private readonly Button _btnCheckEnv = new();
    /// <summary>Строки проверки окружения: у каждой свой цвет, поэтому их несколько.</summary>
    private readonly Label[] _envLines = { new(), new(), new(), new() };

    /// <summary>Подсказки: держим полем, иначе сборщик мусора снимет их вместе с формой.</summary>
    private readonly ToolTip _envTips = new() { AutoPopDelay = 20000, InitialDelay = 400, ShowAlways = true };

    // --- баланс и тариф -----------------------------------------------------
    private readonly Theme.CardPanel _account = new();
    private readonly CheckBox _chkAuto = new();
    private readonly NumericUpDown _numMinutes = new();
    private readonly CheckBox _chkWarn = new();
    private readonly NumericUpDown _numThreshold = new();
    private readonly Label _lblMinutes = new();
    private readonly Label _lblThreshold = new();

    private readonly Theme.CardPanel _tariff = new();
    private readonly Label _lblWindows = new();
    private readonly CheckBox _chkPeakAuto = new();
    private readonly NumericUpDown _numPeakHours = new();
    private readonly Label _lblPeakHours = new();
    private readonly Label _lblHoursSuffix = new();
    private readonly Button _btnCheck = new();
    private readonly Label _lblChecked = new();
    private readonly Label _lblStatus = new();
    private readonly Button _btnApply = new();
    private readonly Button _btnKeep = new();
    private readonly Button _btnPricesBig = new();
    private readonly TextBox _txtPrices = new();

    // --- о панели -----------------------------------------------------------
    private readonly Label _lblAboutName = new();
    private readonly Label _lblAboutVersion = new();
    private readonly Label _lblAboutLicense = new();
    private readonly Label _lblAboutCopyright = new();
    private readonly Label _lblAboutHint = new();
    private readonly Label _lblAboutDonateNote = new();

    private PricingCheckResult _result;
    private bool _busy;
    private bool _suppressLanguage;
    private bool _suppressTheme;

    public SettingsForm(AppSettings settings, AppPaths paths, PricingUpdateService pricing,
        Func<string> currentWindows, PricingCheckResult pending = null, Action<string> onLanguageChanged = null,
        Action<UpdateDownload> onUpdateReady = null, Action<string> onThemeChanged = null)
    {
        _settings = settings;
        _paths = paths;
        _pricing = pricing;
        _currentWindows = currentWindows;
        _onLanguageChanged = onLanguageChanged;
        _onUpdateReady = onUpdateReady;
        _onThemeChanged = onThemeChanged;

        Text = Loc.T("settings.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        // Высота выбрана по содержимому вкладки «Баланс и тариф»: блок «Тариф и цены»
        // с окном предпросмотра цен занимает 506px от верха страницы, а меньшая высота
        // обрезала бы у него нижнюю рамку.
        ClientSize = new Size(620, 592);
        MinimumSize = new Size(636, 632);
        // Шрифт и цвета — только из темы: своих литералов в этом окне не осталось.
        Font = Theme.Body;
        BackColor = Theme.Colors.Window;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        WireEvents();
        LoadValues(pending);

        // Тема — последней: BuildLayout расставляет роли шрифта и отступы, а Theme.Apply
        // обходит дерево целиком и приводит цвета, рамки и вид кнопок к одной палитре.
        Theme.Apply(this);
    }

    /// <summary>Пользователь применил новые окна пика.</summary>
    public bool Applied { get; private set; }

    private void BuildLayout()
    {
        _tabs.Dock = DockStyle.Fill;
        Controls.Add(_tabs);

        foreach (var (page, key) in new[]
                 {
                     (_pageGeneral, "settings.tab.general"),
                     (_pageBalance, "settings.tab.balance"),
                     (_pageAbout, "settings.tab.about"),
                     (_pageUpdate, "update.title"),
                 })
        {
            page.Text = Loc.T(key);
            page.UseVisualStyleBackColor = false;
            page.BackColor = Theme.Colors.Surface;
            page.ForeColor = Theme.Colors.Text;
            _tabs.TabPages.Add(page);
        }

        // Вкладки рисуем сами (Theme.Tabs): выбранная — поверхность с акцентной полосой снизу.
        // Остаётся именно TabControl: от этого зависит обход страниц в --layout-check.
        Theme.Tabs(_tabs);

        // Кнопки окна добавляем до заполнения страниц: панель кнопок отнимает у вкладок
        // высоту, и страницы должны получить уже окончательный размер.
        // --- кнопки окна ---------------------------------------------------
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 46 };

        // «Сохранить» — единственное главное действие окна: акцентная заливка и на два пикселя
        // больше остальных кнопок. Тип PrimaryButton нужен, чтобы Theme.Apply, обходя дерево,
        // не оформил её как вторичную.
        var ok = new Theme.PrimaryButton
        {
            Text = Loc.T("settings.save"),
            DialogResult = DialogResult.OK,
            Location = new Point(500, 8),
            Size = new Size(104, Theme.PrimaryButtonHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Theme.Button(ok, Theme.ButtonKind.Primary);
        ok.Click += (_, _) => ApplyTo(_settings);
        buttons.Controls.Add(ok);

        var cancel = new Button
        {
            Text = Loc.T("settings.close"),
            DialogResult = DialogResult.Cancel,
            Location = new Point(390, 8),
            Size = new Size(100, Theme.ButtonHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Theme.Button(cancel, Theme.ButtonKind.Secondary);
        buttons.Controls.Add(cancel);

        buttons.Resize += (_, _) =>
        {
            ok.Left = buttons.ClientSize.Width - ok.Width - 12;
            cancel.Left = ok.Left - cancel.Width - 8;
        };

        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        SizePages();

        BuildGeneralPage();
        BuildBalancePage();
        BuildAboutPage();
        BuildUpdatePage();
    }

    /// <summary>
    /// Выдаёт страницам вкладок их настоящий размер до расстановки элементов.
    /// Пока страница не разложена, её размер — 200×100 по умолчанию, а Anchor считает
    /// отступы именно от размера родителя в момент добавления элемента. Из-за этого метки
    /// с Anchor растягивались на 300–400px за край окна, и текст уезжал из панели.
    /// </summary>
    private void SizePages()
    {
        try
        {
            _tabs.CreateControl();
            _tabs.PerformLayout();

            var area = _tabs.DisplayRectangle.Size;
            if (area.Width <= 0 || area.Height <= 0) return;

            foreach (TabPage page in _tabs.TabPages) page.Size = area;
        }
        catch
        {
            // Размер получить не удалось — оставляем как есть: при показе окна раскладка
            // поправит страницы, а элементы без Anchor от этого не зависят.
        }
    }

    private void BuildGeneralPage()
    {
        var page = _pageGeneral;

        _lblLanguage.Text = Loc.T("settings.language");
        _lblLanguage.Location = new Point(16, 20);
        _lblLanguage.Size = new Size(220, 20);
        page.Controls.Add(_lblLanguage);

        // Список языков — тот же вид, что и у переключателя темы: оба оформляет Theme.Combo.
        _cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbLanguage.Location = new Point(240, 16);
        _cmbLanguage.Size = new Size(220, Theme.FieldHeight);
        page.Controls.Add(_cmbLanguage);

        _lblLanguageHint.Text = Loc.T("settings.language.applies");
        _lblLanguageHint.ForeColor = Theme.Colors.Faint;
        _lblLanguageHint.Font = Theme.Hint;
        // Подсказка переехала под подпись языка (левая колонка): правая половина этой строки
        // отдана переключателю темы — обе настройки меняют окно сразу, не дожидаясь «Сохранить»,
        // и стоят рядом. Так ни одна координата ниже не сдвинулась.
        _lblLanguageHint.Location = new Point(16, 44);
        _lblLanguageHint.Size = new Size(220, 20);
        page.Controls.Add(_lblLanguageHint);

        _lblTheme.Text = Loc.T("settings.theme");
        _lblTheme.Location = new Point(240, 46);
        _lblTheme.Size = new Size(130, 20);
        page.Controls.Add(_lblTheme);

        _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTheme.Location = new Point(378, 42);
        _cmbTheme.Size = new Size(200, Theme.FieldHeight);
        page.Controls.Add(_cmbTheme);

        _lblWorkDir.Text = Loc.T("settings.workDir");
        _lblWorkDir.Location = new Point(16, 82);
        _lblWorkDir.Size = new Size(220, 20);
        page.Controls.Add(_lblWorkDir);

        _txtWorkDir.BorderStyle = BorderStyle.FixedSingle;
        _txtWorkDir.Location = new Point(240, 78);
        _txtWorkDir.Size = new Size(230, Theme.FieldHeight);
        page.Controls.Add(_txtWorkDir);

        // Кнопка поднята на 3px: она выросла до высоты кнопки темы (32px) и в прежней строке
        // задевала подпись под полем.
        _btnWorkDir.Text = Loc.T("settings.browse");
        _btnWorkDir.Location = new Point(478, 74);
        _btnWorkDir.Size = new Size(100, Theme.ButtonHeight);
        _btnWorkDir.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Theme.Button(_btnWorkDir, Theme.ButtonKind.Secondary);
        page.Controls.Add(_btnWorkDir);

        _lblWorkDirHint.Text = Loc.T("settings.workDirHint");
        _lblWorkDirHint.ForeColor = Theme.Colors.Faint;
        _lblWorkDirHint.Font = Theme.Hint;
        _lblWorkDirHint.Location = new Point(16, 108);
        _lblWorkDirHint.Size = new Size(562, 20);
        _lblWorkDirHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lblWorkDirHint);

        _lblPort.Text = Loc.T("settings.port");
        _lblPort.Location = new Point(16, 140);
        _lblPort.Size = new Size(220, 20);
        page.Controls.Add(_lblPort);

        _numPort.Minimum = 1;
        _numPort.Maximum = 65535;
        _numPort.Value = 3080;
        _numPort.Location = new Point(240, 136);
        _numPort.Size = new Size(90, Theme.FieldHeight);
        page.Controls.Add(_numPort);

        _lblPortState.Location = new Point(340, 140);
        _lblPortState.Size = new Size(240, 20);
        page.Controls.Add(_lblPortState);

        page.Controls.Add(new Label
        {
            Text = Loc.T("settings.portChanged"),
            ForeColor = Theme.Colors.Faint,
            Font = Theme.Hint,
            Location = new Point(16, 162),
            Size = new Size(562, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        _lblNodePath.Text = Loc.T("settings.nodePath");
        _lblNodePath.Location = new Point(16, 190);
        _lblNodePath.Size = new Size(220, 20);
        page.Controls.Add(_lblNodePath);

        _txtNodePath.BorderStyle = BorderStyle.FixedSingle;
        _txtNodePath.Location = new Point(240, 186);
        _txtNodePath.Size = new Size(338, Theme.FieldHeight);
        _txtNodePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_txtNodePath);

        _lblDshPath.Text = Loc.T("settings.dshPath");
        _lblDshPath.Location = new Point(16, 222);
        _lblDshPath.Size = new Size(220, 20);
        page.Controls.Add(_lblDshPath);

        _txtDshPath.BorderStyle = BorderStyle.FixedSingle;
        _txtDshPath.Location = new Point(240, 218);
        _txtDshPath.Size = new Size(338, Theme.FieldHeight);
        _txtDshPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_txtDshPath);

        _lblEnvHint.Text = Loc.T("settings.envHint");
        _lblEnvHint.ForeColor = Theme.Colors.Faint;
        _lblEnvHint.Font = Theme.Hint;
        _lblEnvHint.Location = new Point(16, 252);
        _lblEnvHint.Size = new Size(562, 34);
        _lblEnvHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lblEnvHint);

        _btnCheckEnv.Text = Loc.T("settings.checkEnv");
        _btnCheckEnv.Location = new Point(16, 292);
        _btnCheckEnv.Size = new Size(180, Theme.ButtonHeight);
        Theme.Button(_btnCheckEnv, Theme.ButtonKind.Secondary);
        page.Controls.Add(_btnCheckEnv);

        // Строки проверки окружения — по метке на строку: у каждой свой цвет (зелёный — нашлось,
        // янтарный — нет). Раньше это была одна метка с общим цветом, и при одной недостающей
        // строке зелёная «Node найден» тоже становилась янтарной (это и заметил владелец).
        for (var index = 0; index < _envLines.Length; index++)
        {
            var line = _envLines[index];
            line.AutoSize = false;
            line.Location = new Point(16, 330 + index * 20);
            line.Size = new Size(562, 20);
            line.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(line);
        }

        FillThemes();
    }

    private void BuildBalancePage()
    {
        var page = _pageBalance;

        // Карточка вместо серой рамки GroupBox: заголовок рисует сама панель (Theme.CardPanel.Title)
        // внутри рамки, поэтому содержимое опущено на CardHead — иначе подпись легла бы на
        // первый флажок. Заголовок — свойство панели, а не метка в y=0: так верхний отступ
        // карточки остаётся отступом из темы, а не нулём.
        const int CardHead = 6;

        // --- баланс --------------------------------------------------------
        _account.Title = Loc.T("settings.groupBalance");
        _account.Location = new Point(12, 10);
        _account.Size = new Size(576, 190);
        _account.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_account);

        _chkAuto.Text = Loc.T("settings.autoRefresh");
        _chkAuto.Location = new Point(16, 26 + CardHead);
        _chkAuto.Size = new Size(370, 22);
        _account.Controls.Add(_chkAuto);

        _lblMinutes.Text = Loc.T("settings.refreshEvery");
        _lblMinutes.Location = new Point(34, 56 + CardHead);
        _lblMinutes.Size = new Size(260, 20);
        _account.Controls.Add(_lblMinutes);

        _numMinutes.Minimum = 1;
        _numMinutes.Maximum = 1440;
        _numMinutes.Value = 5;
        _numMinutes.Location = new Point(300, 52 + CardHead);
        _numMinutes.Size = new Size(80, Theme.FieldHeight);
        _account.Controls.Add(_numMinutes);

        _chkWarn.Text = Loc.T("settings.warnLow");
        _chkWarn.Location = new Point(16, 92 + CardHead);
        _chkWarn.Size = new Size(370, 22);
        _account.Controls.Add(_chkWarn);

        _lblThreshold.Text = Loc.T("settings.threshold");
        _lblThreshold.Location = new Point(34, 122 + CardHead);
        _lblThreshold.Size = new Size(260, 20);
        _account.Controls.Add(_lblThreshold);

        _numThreshold.DecimalPlaces = 2;
        _numThreshold.Minimum = 0;
        _numThreshold.Maximum = 1_000_000;
        _numThreshold.Increment = 0.5m;
        _numThreshold.Value = 5m;
        _numThreshold.Location = new Point(300, 118 + CardHead);
        _numThreshold.Size = new Size(80, Theme.FieldHeight);
        _account.Controls.Add(_numThreshold);

        _account.Controls.Add(new Label
        {
            Text = Loc.T("settings.thresholdHint"),
            ForeColor = Theme.Colors.Faint,
            Font = Theme.Hint,
            Location = new Point(18, 150 + CardHead),
            Size = new Size(544, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        // --- тариф и цены --------------------------------------------------
        _tariff.Title = Loc.T("settings.groupTariff");
        _tariff.Location = new Point(12, 206);
        _tariff.Size = new Size(576, 300);
        _tariff.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_tariff);

        _lblWindows.Text = Loc.T("common.dash");
        _lblWindows.Location = new Point(16, 22 + CardHead);
        _lblWindows.Size = new Size(544, 34);
        _lblWindows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _tariff.Controls.Add(_lblWindows);

        _chkPeakAuto.Text = Loc.T("settings.peakAuto");
        _chkPeakAuto.Location = new Point(16, 60 + CardHead);
        _chkPeakAuto.Size = new Size(330, 22);
        _tariff.Controls.Add(_chkPeakAuto);

        _lblPeakHours.Text = Loc.T("settings.every");
        _lblPeakHours.ForeColor = Theme.Colors.Muted;
        _lblPeakHours.Location = new Point(352, 62 + CardHead);
        _lblPeakHours.Size = new Size(48, 20);
        _tariff.Controls.Add(_lblPeakHours);

        _numPeakHours.Minimum = 1;
        _numPeakHours.Maximum = 720;
        _numPeakHours.Value = 24;
        _numPeakHours.Location = new Point(404, 60 + CardHead);
        _numPeakHours.Size = new Size(60, Theme.FieldHeight);
        _tariff.Controls.Add(_numPeakHours);

        _lblHoursSuffix.Text = Loc.T("settings.hours");
        _lblHoursSuffix.ForeColor = Theme.Colors.Muted;
        _lblHoursSuffix.Location = new Point(470, 62 + CardHead);
        _lblHoursSuffix.Size = new Size(40, 20);
        _tariff.Controls.Add(_lblHoursSuffix);

        _btnCheck.Text = Loc.T("settings.check");
        _btnCheck.Location = new Point(16, 90 + CardHead);
        _btnCheck.Size = new Size(200, Theme.ButtonHeight);
        Theme.Button(_btnCheck, Theme.ButtonKind.Secondary);
        _tariff.Controls.Add(_btnCheck);

        _lblChecked.Text = "";
        _lblChecked.ForeColor = Theme.Colors.Faint;
        _lblChecked.TextAlign = ContentAlignment.MiddleRight;
        _lblChecked.Location = new Point(340, 96 + CardHead);
        _lblChecked.Size = new Size(220, 22);
        _lblChecked.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _tariff.Controls.Add(_lblChecked);

        _lblStatus.Text = Loc.T("settings.status.idle");
        _lblStatus.Location = new Point(16, 130 + CardHead);
        _lblStatus.Size = new Size(544, 46);
        _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _tariff.Controls.Add(_lblStatus);

        _btnApply.Text = Loc.T("settings.apply");
        _btnApply.Location = new Point(16, 180 + CardHead);
        _btnApply.Size = new Size(140, Theme.ButtonHeight);
        _btnApply.Enabled = false;
        Theme.Button(_btnApply, Theme.ButtonKind.Secondary);
        _tariff.Controls.Add(_btnApply);

        _btnKeep.Text = Loc.T("settings.keep");
        _btnKeep.Location = new Point(166, 180 + CardHead);
        _btnKeep.Size = new Size(160, Theme.ButtonHeight);
        _btnKeep.Enabled = false;
        Theme.Button(_btnKeep, Theme.ButtonKind.Secondary);
        _tariff.Controls.Add(_btnKeep);

        _tariff.Controls.Add(new Label
        {
            Text = Loc.T("settings.pricesHint"),
            ForeColor = Theme.Colors.Muted,
            Font = Theme.Hint,
            Location = new Point(16, 220 + CardHead),
            Size = new Size(300, 18),
        });

        _btnPricesBig.Text = Loc.T("settings.pricesBig");
        _btnPricesBig.Location = new Point(420, 208 + CardHead);
        _btnPricesBig.Size = new Size(140, Theme.ButtonHeight);
        _btnPricesBig.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Theme.Button(_btnPricesBig, Theme.ButtonKind.Secondary);
        _tariff.Controls.Add(_btnPricesBig);

        _txtPrices.Multiline = true;
        _txtPrices.ReadOnly = true;
        _txtPrices.ScrollBars = ScrollBars.Vertical;
        _txtPrices.BorderStyle = BorderStyle.FixedSingle;
        // Колонки прайса читаются только моноширинным шрифтом — это роль Mono из темы.
        _txtPrices.Font = Theme.Mono9;
        _txtPrices.Location = new Point(16, 240 + CardHead);
        _txtPrices.Size = new Size(544, 42);
        _txtPrices.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _txtPrices.Text = Loc.T("settings.pricesPlaceholder");
        _tariff.Controls.Add(_txtPrices);
    }

    private void BuildAboutPage()
    {
        var page = _pageAbout;

        _lblAboutName.Text = Loc.T("about.name");
        _lblAboutName.Location = new Point(16, 20);
        _lblAboutName.Size = new Size(560, 40);
        _lblAboutName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lblAboutName);

        _lblAboutVersion.Text = Loc.T("about.version", AppVersion.Short);
        _lblAboutVersion.Location = new Point(16, 72);
        _lblAboutVersion.Size = new Size(560, 20);
        page.Controls.Add(_lblAboutVersion);

        _lblAboutLicense.Text = Loc.T("about.license");
        _lblAboutLicense.Location = new Point(16, 96);
        _lblAboutLicense.Size = new Size(560, 20);
        page.Controls.Add(_lblAboutLicense);

        _lblAboutCopyright.Text = Loc.T("about.copyright");
        _lblAboutCopyright.Location = new Point(16, 120);
        _lblAboutCopyright.Size = new Size(560, 20);
        page.Controls.Add(_lblAboutCopyright);

        _lblAboutHint.ForeColor = Theme.Colors.Faint;
        _lblAboutHint.Font = Theme.Hint;
        _lblAboutHint.Text = Loc.T("about.hint");
        _lblAboutHint.Location = new Point(16, 160);
        _lblAboutHint.Size = new Size(560, 80);
        _lblAboutHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lblAboutHint);

        // Ссылки: исходники с выпусками и — если владелец её заполнил — поддержка проекта.
        // Донат-строка появляется только при непустой ссылке: кнопка в никуда хуже её отсутствия.
        _lnkAboutRepo.Text = Loc.T("about.repo");
        _lnkAboutRepo.Location = new Point(16, 250);
        _lnkAboutRepo.Size = new Size(360, 22);
        _lnkAboutRepo.LinkClicked += (_, _) => ShowLink(AppLinks.RepositoryUrl);
        page.Controls.Add(_lnkAboutRepo);

        if (AppLinks.Donate.Length > 0)
        {
            _lnkAboutDonate.Text = Loc.T("about.donate");
            _lnkAboutDonate.Location = new Point(16, 276);
            _lnkAboutDonate.Size = new Size(360, 22);
            _lnkAboutDonate.LinkClicked += (_, _) => ShowLink(AppLinks.Donate);
            page.Controls.Add(_lnkAboutDonate);

            // Пояснение рядом со ссылкой: почему донаты вообще есть и на что они идут. Ширина
            // задана (AutoSize = false) — так строка переносится, а проверка вёрстки умеет
            // сверить, хватает ли высоты и не шире ли метки самое длинное слово.
            _lblAboutDonateNote.AutoSize = false;
            _lblAboutDonateNote.ForeColor = Theme.Colors.Faint;
            _lblAboutDonateNote.Font = Theme.Hint;
            _lblAboutDonateNote.Text = Loc.T("about.donateNote");
            _lblAboutDonateNote.Location = new Point(16, 302);
            _lblAboutDonateNote.Size = new Size(560, 60);
            _lblAboutDonateNote.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(_lblAboutDonateNote);
        }

        FillLanguages();
    }

    private void ShowLink(string url)
    {
        var error = AppLinks.Open(url);
        if (error.Length > 0) MessageBox.Show(this, error, Loc.T("settings.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Вкладка «Обновления»: что стоит сейчас, что лежит на GitHub, когда проверяли и что
    /// нового в выпуске (выжимка из CHANGELOG). Человек сам решает, обновляться ли.
    /// </summary>
    private void BuildUpdatePage()
    {
        var page = _pageUpdate;

        _lblUpdateCurrent.Location = new Point(16, 20);
        _lblUpdateCurrent.Size = new Size(560, 20);
        page.Controls.Add(_lblUpdateCurrent);

        _lblUpdateGithub.Location = new Point(16, 46);
        _lblUpdateGithub.Size = new Size(560, 20);
        _lblUpdateGithub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lblUpdateGithub);

        _lblUpdateChecked.ForeColor = Theme.Colors.Faint;
        _lblUpdateChecked.Font = Theme.Hint;
        _lblUpdateChecked.Location = new Point(16, 72);
        _lblUpdateChecked.Size = new Size(560, 20);
        _lblUpdateChecked.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lblUpdateChecked);

        _btnUpdateCheck.Text = Loc.T("update.check");
        _btnUpdateCheck.Location = new Point(16, 102);
        _btnUpdateCheck.Size = new Size(160, Theme.ButtonHeight);
        _btnUpdateCheck.Click += async (_, _) => await CheckUpdatesAsync();
        Theme.Button(_btnUpdateCheck, Theme.ButtonKind.Secondary);
        page.Controls.Add(_btnUpdateCheck);

        _btnUpdateInstall.Text = Loc.T("update.install");
        _btnUpdateInstall.Location = new Point(186, 102);
        _btnUpdateInstall.Size = new Size(200, Theme.ButtonHeight);
        _btnUpdateInstall.Enabled = false;
        _btnUpdateInstall.Click += async (_, _) => await InstallUpdateAsync();
        Theme.Button(_btnUpdateInstall, Theme.ButtonKind.Secondary);
        page.Controls.Add(_btnUpdateInstall);

        _btnUpdatePage.Text = Loc.T("update.openPage");
        _btnUpdatePage.Location = new Point(396, 102);
        _btnUpdatePage.Size = new Size(180, Theme.ButtonHeight);
        _btnUpdatePage.Enabled = false;
        _btnUpdatePage.Click += (_, _) => ShowLink(_lastUpdate?.PageUrl ?? _settings.UpdatePageUrl);
        Theme.Button(_btnUpdatePage, Theme.ButtonKind.Secondary);
        page.Controls.Add(_btnUpdatePage);

        _lblUpdateNotes.Text = Loc.T("update.notes");
        _lblUpdateNotes.Location = new Point(16, 146);
        _lblUpdateNotes.Size = new Size(560, 18);
        page.Controls.Add(_lblUpdateNotes);

        _txtUpdateNotes.Multiline = true;
        _txtUpdateNotes.ReadOnly = true;
        _txtUpdateNotes.ScrollBars = ScrollBars.Vertical;
        _txtUpdateNotes.BorderStyle = BorderStyle.FixedSingle;
        // Заметки выпуска — текст с разметкой и колонками: роль Mono (оформленный рендер
        // Markdown — следующая волна, сейчас поле только приведено к палитре и роли).
        _txtUpdateNotes.Font = Theme.Mono9;
        _txtUpdateNotes.Location = new Point(16, 168);
        // Высота задана числом, а Anchor у неё только верхний: низ поля не тянется вниз.
        // Так подсказка и полоса прогресса под ним не могут оказаться поверх текста ни при
        // каком размере окна — низ окна занят ими, и они не зависят от размера поля.
        _txtUpdateNotes.Size = new Size(560, 168);
        _txtUpdateNotes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_txtUpdateNotes);

        // Подсказка — между полем заметок (до 336) и полосой прогресса (368): своей строкой,
        // без наложения. Координаты посчитаны от САМОГО УЗКОГО окна (страница 546px высотой):
        // подсказка 340…358, полоса 368…380, состояние 388…448 — всё внутри 546. Обе нижние
        // метки и полоса привязаны к низу, поэтому запас не меняется при растягивании окна.
        // Проверяет это --layout-check: подсказка видима, значит её текст измеряется, и
        // CheckOverlaps видит её рядом с полем заметок.
        _lblUpdateNotesHint.ForeColor = Theme.Colors.Muted;
        _lblUpdateNotesHint.Font = Theme.Hint;
        _lblUpdateNotesHint.Text = Loc.T("update.notesHint");
        _lblUpdateNotesHint.Location = new Point(16, 340);
        _lblUpdateNotesHint.Size = new Size(560, 18);
        _lblUpdateNotesHint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        page.Controls.Add(_lblUpdateNotesHint);

        _barUpdate.Location = new Point(16, 368);
        _barUpdate.Size = new Size(560, 12);
        _barUpdate.Style = ProgressBarStyle.Marquee;
        _barUpdate.MarqueeAnimationSpeed = 30;
        _barUpdate.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _barUpdate.Visible = false;
        page.Controls.Add(_barUpdate);

        _lblUpdateState.ForeColor = Theme.Colors.Muted;
        _lblUpdateState.Font = Theme.Hint;
        _lblUpdateState.Location = new Point(16, 388);
        _lblUpdateState.Size = new Size(560, 60);
        _lblUpdateState.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        page.Controls.Add(_lblUpdateState);

        FillUpdates();
    }

    /// <summary>Показывает то, что уже знаем из настроек — без обращения к сети.</summary>
    private void FillUpdates()
    {
        _lblUpdateCurrent.Text = Loc.T("update.current", AppVersion.Short);
        _lblUpdateGithub.Text = _settings.UpdateLatest.Length == 0
            ? Loc.T("update.notChecked")
            : Loc.T("update.onGithub", _settings.UpdateLatest + (_settings.UpdatePublished.Length > 0 ? " (" + _settings.UpdatePublished + ")" : ""));
        _lblUpdateChecked.Text = _settings.UpdateCheckedAt.HasValue
            ? Loc.T("update.checkedAt", _settings.UpdateCheckedAt.Value.ToString("dd.MM.yyyy HH:mm"))
            : Loc.T("update.notChecked");

        var notes = NotesForDisplay();
        _txtUpdateNotes.Text = notes.Length > 0 ? notes : Loc.T("update.noNotes");
        _btnUpdatePage.Enabled = _settings.UpdatePageUrl.Length > 0;

        // Кнопка обновления включается только после проверки: без неё неизвестно, что скачивать.
        _btnUpdateInstall.Enabled = UpdateService.IsNewer(_settings.UpdateLatest, AppVersion.Short);
    }

    /// <summary>
    /// Заметки для показа на ТЕКУЩЕМ языке интерфейса. Блок выбирается здесь, при показе, а не
    /// тогда, когда пришёл ответ GitHub: язык можно сменить в любой момент, а проверка
    /// обновлений бывает не чаще раза в сутки — без этого в поле висел бы прежний язык, хотя
    /// подсказка обещает язык интерфейса. Сырое тело для этого и хранится в настройках.
    ///
    /// Если сырого тела нет (настройки от панели до 1.21.0, где лежал готовый блок), показываем
    /// то, что есть: меток в старом значении нет, и разбор вернёт его же целиком.
    /// </summary>
    private string NotesForDisplay()
    {
        var raw = _settings.UpdateNotesRaw;
        return raw.Length > 0
            ? UpdateService.PickNotesForPanel(raw, Loc.Language)
            : _settings.UpdateNotes;
    }

    /// <summary>Спрашивает GitHub и запоминает ответ, чтобы он был виден и при следующем открытии.</summary>
    private async Task CheckUpdatesAsync()
    {
        if (_updating) return;

        SetUpdateBusy(true, Loc.T("update.checking"));
        try
        {
            var check = await Task.Run(UpdateService.Check);
            _lastUpdate = check;

            if (check.Ok)
            {
                _settings.UpdateCheckedAt = check.CheckedAt;
                _settings.UpdateLatest = check.Latest;
                _settings.UpdatePublished = check.PublishedAt == default ? "" : check.PublishedAt.ToString("yyyy-MM-dd");
                _settings.UpdatePageUrl = check.PageUrl;
                _settings.UpdateNotesRaw = UpdateService.RawNotes(check.Notes);
                _settings.UpdateNotes = UpdateService.PickNotesForPanel(_settings.UpdateNotesRaw, Loc.Language);
                _settings.Save(_paths.SettingsPath);
            }

            FillUpdates();
            _lblUpdateState.ForeColor = check.Ok && check.Newer ? Theme.Colors.Warning : Theme.Colors.Muted;
            _lblUpdateState.Text = check.Summary();
        }
        catch (Exception error)
        {
            _lblUpdateState.ForeColor = Theme.Colors.Warning;
            _lblUpdateState.Text = Loc.T("update.failed", error.Message);
        }
        finally
        {
            SetUpdateBusy(false, null);
        }
    }

    /// <summary>Скачивает выпуск, готовит замену файлов и просит панель перезапуститься.</summary>
    private async Task InstallUpdateAsync()
    {
        if (_updating) return;

        var check = _lastUpdate;
        if (check == null || !check.Ok || !check.Newer)
        {
            // Решение могло быть принято в прошлый раз: тогда спрашиваем GitHub снова.
            SetUpdateBusy(true, Loc.T("update.checking"));
            try
            {
                check = await Task.Run(UpdateService.Check);
                _lastUpdate = check;
            }
            finally
            {
                SetUpdateBusy(false, null);
            }
        }

        if (check == null || !check.Ok)
        {
            _lblUpdateState.ForeColor = Theme.Colors.Warning;
            _lblUpdateState.Text = check?.Summary() ?? Loc.T("update.notChecked");
            return;
        }

        if (!check.Newer)
        {
            _lblUpdateState.ForeColor = Theme.Colors.Muted;
            _lblUpdateState.Text = Loc.T("update.alreadyLatest");
            return;
        }

        if (MessageBox.Show(this, Loc.T("update.ask", check.Latest), Loc.T("update.title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        SetUpdateBusy(true, Loc.T("update.downloading", check.AssetName));
        try
        {
            var download = await Task.Run(() => UpdateService.Prepare(check, _paths, step => BeginInvoke((Action)(() => _lblUpdateState.Text = step))));
            if (!download.Ok)
            {
                _lblUpdateState.ForeColor = Theme.Colors.Warning;
                _lblUpdateState.Text = Loc.T("update.failed", download.Error);
                return;
            }

            _lblUpdateState.ForeColor = Theme.Colors.Success;
            _lblUpdateState.Text = Loc.T("update.ready", download.Version, download.BackupFolder);

            // Панель закрывает и запускает заново уже сценарий обновления.
            _onUpdateReady?.Invoke(download);
        }
        catch (Exception error)
        {
            _lblUpdateState.ForeColor = Theme.Colors.Warning;
            _lblUpdateState.Text = Loc.T("update.failed", error.Message);
        }
        finally
        {
            SetUpdateBusy(false, null);
        }
    }

    private void SetUpdateBusy(bool busy, string state)
    {
        _updating = busy;
        _barUpdate.Visible = busy;
        _btnUpdateCheck.Enabled = !busy;
        _btnUpdateInstall.Enabled = !busy && UpdateService.IsNewer(_settings.UpdateLatest, AppVersion.Short);
        UseWaitCursor = busy;
        if (state != null)
        {
            _lblUpdateState.ForeColor = Theme.Colors.Muted;
            _lblUpdateState.Text = state;
        }
    }

    /// <summary>Список языков: «Авто» плюс три словаря; названия языков не переводим.</summary>
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

    /// <summary>
    /// Три положения темы в том же порядке, что и ThemeCodes. Выбор берём из настройки, а не из
    /// текущей палитры: «как в Windows» и «светлая» в светлой системе дают одинаковый вид, и по
    /// палитре их не различить — а после смены языка окно пересобирается и обязано показать то,
    /// что человек выбрал.
    /// </summary>
    private void FillThemes()
    {
        _suppressTheme = true;
        try
        {
            _cmbTheme.Items.Clear();
            _cmbTheme.Items.Add(Loc.T("settings.theme.auto"));
            _cmbTheme.Items.Add(Loc.T("settings.theme.light"));
            _cmbTheme.Items.Add(Loc.T("settings.theme.dark"));

            var index = Array.IndexOf(ThemeCodes, Theme.ModeTo(Theme.ModeFrom(_settings.ThemeMode)));
            _cmbTheme.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressTheme = false;
        }
    }

    private void WireEvents()
    {
        _chkAuto.CheckedChanged += (_, _) => SyncEnabled();
        _chkWarn.CheckedChanged += (_, _) => SyncEnabled();
        _chkPeakAuto.CheckedChanged += (_, _) => SyncEnabled();
        _btnCheck.Click += async (_, _) => await CheckAsync();
        _btnApply.Click += (_, _) => ApplyPending();
        _btnKeep.Click += (_, _) => ClearPending(Loc.T("settings.kept"));
        _btnPricesBig.Click += (_, _) => ShowPricesBig();
        _btnWorkDir.Click += (_, _) => PickWorkDir();
        _numPort.ValueChanged += (_, _) => CheckPort();
        _btnCheckEnv.Click += (_, _) => CheckEnvironment();

        _cmbLanguage.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressLanguage) return;

            var code = LanguageCodes[Math.Max(0, _cmbLanguage.SelectedIndex)];
            _onLanguageChanged?.Invoke(code);

            // Язык применяется сразу: окно пересобирает свои подписи, сохраняя выбор,
            // который человек уже сделал в полях.
            Retext();
        };

        _cmbTheme.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressTheme) return;

            var code = ThemeCodes[Math.Max(0, _cmbTheme.SelectedIndex)];

            // Тема применяется сразу, как и язык: панель переключает палитру и пересобирает своё
            // окно, а это окно пересобирает себя ниже — иначе о смене узнало бы только оно.
            _onThemeChanged?.Invoke(code);
            Retext();
        };
    }

    /// <summary>Пересобирает окно на новом языке или в новой теме, не теряя выставленные значения.</summary>
    public void Retext()
    {
        var auto = _chkAuto.Checked;
        var minutes = _numMinutes.Value;
        var warn = _chkWarn.Checked;
        var threshold = _numThreshold.Value;
        var peakAuto = _chkPeakAuto.Checked;
        var peakHours = _numPeakHours.Value;
        var workDir = _txtWorkDir.Text;
        var port = _numPort.Value;
        var nodePath = _txtNodePath.Text;
        var dshPath = _txtDshPath.Text;
        var applied = _result;
        var tab = _tabs.SelectedIndex;

        SuspendLayout();
        try
        {
            Text = Loc.T("settings.title");
            Font = Theme.Body;
            Controls.Clear();
            _pageGeneral.Controls.Clear();
            _pageBalance.Controls.Clear();
            _pageAbout.Controls.Clear();
            // Карточки чистим вместе со страницами: их содержимое добавляет BuildBalancePage,
            // и без этого каждая пересборка (смена языка, а теперь и темы) оставляла бы в
            // карточке ещё один слой тех же подписей — по одной на каждую смену.
            _account.Controls.Clear();
            _tariff.Controls.Clear();
            _tabs.TabPages.Clear();
            BuildLayout();

            _chkAuto.Checked = auto;
            _numMinutes.Value = minutes;
            _chkWarn.Checked = warn;
            _numThreshold.Value = threshold;
            _chkPeakAuto.Checked = peakAuto;
            _numPeakHours.Value = peakHours;
            _txtWorkDir.Text = workDir;
            _numPort.Value = port;
            _txtNodePath.Text = nodePath;
            _txtDshPath.Text = dshPath;

            SyncEnabled();
            RefreshCurrent();
            CheckPort();
            _lblStatus.Text = Loc.T("settings.status.idle");

            if (applied != null && applied.Ok && applied.Differs)
            {
                _result = applied;
                ShowPending(applied);
            }

            if (tab >= 0 && tab < _tabs.TabPages.Count) _tabs.SelectedIndex = tab;

            // Тема применяется внутри сборки окна: Retext собирает контролы заново, и без этого
            // после смены языка вид вернулся бы к системным цветам (отдельный пункт приёмки).
            Theme.Apply(this);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    /// <summary>Открывает большие окно с ценами (растягивается как обычное окно).</summary>
    private void ShowPricesBig()
    {
        try
        {
            using var window = new PricesWindow(_result, _result?.SourceUrl)
            {
                StartPosition = FormStartPosition.CenterParent,
            };
            window.ShowDialog(this);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Loc.T("settings.groupTariff"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PickWorkDir()
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Loc.T("settings.workDir"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(_txtWorkDir.Text) ? _txtWorkDir.Text : "",
            };

            if (dialog.ShowDialog(this) == DialogResult.OK) _txtWorkDir.Text = dialog.SelectedPath;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Loc.T("settings.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Свободен ли выбранный порт — видно до сохранения, а не после перезапуска.</summary>
    private void CheckPort()
    {
        var port = (int)_numPort.Value;
        var (owner, name, ours) = ServerController.Listener(port, _paths);

        if (ours)
        {
            // Держит наша же панель: это не помеха, предупреждать не о чем.
            _lblPortState.ForeColor = Theme.Colors.Success;
            _lblPortState.Text = Loc.T("settings.portOurs", owner);
        }
        else if (owner > 0)
        {
            _lblPortState.ForeColor = Theme.Colors.Warning;
            _lblPortState.Text = Loc.T("settings.portBusy", name, owner);
        }
        else
        {
            _lblPortState.ForeColor = Theme.Colors.Success;
            _lblPortState.Text = Loc.T("settings.portFree");
        }
    }

    /// <summary>
    /// Проверка окружения: короткий итог по пунктам — что нашлось, что нет и что с этим делать.
    /// Полные пути в окно не пишем: человеку они ничего не говорят, а места занимают много.
    /// Подробности с путями кладём в подсказку к этому тексту — они нужны, только когда
    /// что-то не нашлось и надо разбираться.
    /// </summary>
    private void CheckEnvironment()
    {
        NodeLocator.ApplyOverrides(_txtNodePath.Text, _txtDshPath.Text);

        // Каждая строка — своя метка и свой цвет: нашлось — зелёным, нет — янтарным,
        // ключ модели — серым (до первого входа его и не должно быть), итог — серым.
        var lines = new List<(string Text, Color Color)>();
        var details = new StringBuilder();

        try
        {
            var node = AppPaths.Display(NodeLocator.ResolveNode());
            var version = NodeVersion(node);
            lines.Add(("✓ " + Loc.T(version.Length > 0 ? "env.short.nodeOk" : "env.short.nodeOkNoVersion", version), Theme.Colors.Success));
            details.AppendLine(Loc.T("onboard.env.nodeFound", node));
        }
        catch
        {
            lines.Add(("• " + Loc.T("env.short.nodeMissing"), Theme.Colors.Warning));
            details.AppendLine(Loc.T("onboard.env.nodeMissing"));
        }

        try
        {
            var bin = AppPaths.Display(NodeLocator.ResolveDshBin());
            var version = DshVersion(bin);
            lines.Add(("✓ " + Loc.T(version.Length > 0 ? "env.short.dshOk" : "env.short.dshOkNoVersion", version), Theme.Colors.Success));
            details.AppendLine(Loc.T("onboard.env.dshFound", bin));
        }
        catch
        {
            lines.Add(("• " + Loc.T("env.short.dshMissing"), Theme.Colors.Warning));
            details.AppendLine(Loc.T("onboard.env.dshMissing"));
        }

        // Ключ модели — не про панель: его сохраняет сам Harness, когда в нём входят.
        // Путь показываем настоящий: %USERPROFILE% в подсказке читается как незаполненная
        // переменная, а не как путь.
          var credentials = AppPaths.CredentialsPath;

          // Одного наличия файла мало: после наката демонстрационной копии панель показывала
          // «ключ на месте», хотя ключа не было — в файле лежала заглушка. Поэтому смотрим,
          // есть ли в файле хоть одно непустое значение.
          var keyFound = false;
          var filePresent = File.Exists(credentials);
          if (filePresent)
          {
              try
              {
                  keyFound = File.ReadAllLines(credentials).Any(line =>
                  {
                      var trimmed = line.Trim().TrimStart('-').Trim();
                      var colon = trimmed.IndexOf(':');
                      return colon > 0 && trimmed[(colon + 1)..].Trim().Trim('"', '\'').Length > 0;
                  });
              }
              catch
              {
                  keyFound = false;
              }
          }

          lines.Add((keyFound
                  ? "✓ " + Loc.T("env.short.keyFound")
                  : "• " + Loc.T(filePresent ? "env.short.keyEmpty" : "env.short.keyMissing"),
              keyFound ? Theme.Colors.Success : Theme.Colors.Faint));
          details.AppendLine(keyFound
              ? Loc.T("env.credentialsFound", credentials)
              : Loc.T(filePresent ? "env.credentialsEmpty" : "balance.keyNotSet") + " (" + credentials + ")");

        lines.Add((Loc.T("env.short.done", DateTime.Now.ToString("HH:mm:ss")), Theme.Colors.Faint));

        for (var index = 0; index < _envLines.Length; index++)
        {
            var line = _envLines[index];
            if (index < lines.Count)
            {
                line.ForeColor = lines[index].Color;
                line.Text = lines[index].Text;
            }
            else
            {
                line.Text = "";
            }

            _envTips.SetToolTip(line, details.ToString().TrimEnd());
        }
    }

    /// <summary>Версия node.exe — «v26.9.0» без ведущей буквы; пусто, если не спросилось.</summary>
    private static string NodeVersion(string nodePath)
    {
        var raw = FirstLine(Environment.ExpandEnvironmentVariables(nodePath), "--version");
        return raw.TrimStart('v', 'V');
    }

    /// <summary>Версия пакета dsh из его package.json рядом с найденным lib\bin.js.</summary>
    private static string DshVersion(string binPath)
    {
        try
        {
            // %USERPROFILE%\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\lib\bin.js → корень пакета
            var directory = new DirectoryInfo(Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(binPath)) ?? "");
            for (var level = 0; level < 3 && directory != null; level++)
            {
                var package = Path.Combine(directory.FullName, "package.json");
                if (File.Exists(package))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(package));
                    return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "";
                }

                directory = directory.Parent;
            }
        }
        catch
        {
            // Версия — украшение: не прочиталась, значит просто не показываем.
        }

        return "";
    }

    /// <summary>Первая строка вывода программы — так узнаём версии node.</summary>
    private static string FirstLine(string executable, string arguments)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return "";

            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(info);
            if (process == null) return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Replace("\r", "").Split('\n')[0].Trim();
        }
        catch
        {
            return "";
        }
    }

    private void LoadValues(PricingCheckResult pending)
    {
        _chkAuto.Checked = _settings.BalanceAutoRefresh;
        _numMinutes.Value = Math.Clamp(_settings.BalanceRefreshMinutes, 1, 1440);
        _chkWarn.Checked = _settings.BalanceWarnEnabled;
        _numThreshold.Value = Math.Clamp(_settings.BalanceWarnThreshold, 0, 1_000_000);
        _chkPeakAuto.Checked = _settings.PeakAutoCheck;
        _numPeakHours.Value = Math.Clamp(_settings.PeakCheckHours, 1, 720);
        _txtWorkDir.Text = _settings.ServerWorkingDir ?? "";
        _numPort.Value = Math.Clamp(_settings.ServerPort, 1, 65535);
        _txtNodePath.Text = _settings.NodePath ?? "";
        _txtDshPath.Text = _settings.DshBinPath ?? "";
        SyncEnabled();
        RefreshCurrent();
        CheckEnvironment();
        CheckPort();

        if (pending != null && pending.Ok && pending.Differs)
        {
            _result = pending;
            ShowPending(pending);
        }
    }

    private void RefreshCurrent()
    {
        try
        {
            _lblWindows.Text = Loc.T("settings.current", _currentWindows());
        }
        catch
        {
            _lblWindows.Text = Loc.T("settings.currentNone");
        }
    }

    private void SyncEnabled()
    {
        _numMinutes.Enabled = _chkAuto.Checked;
        _lblMinutes.Enabled = _chkAuto.Checked;
        _numThreshold.Enabled = _chkWarn.Checked;
        _lblThreshold.Enabled = _chkWarn.Checked;
        _numPeakHours.Enabled = _chkPeakAuto.Checked;
        _lblPeakHours.Enabled = _chkPeakAuto.Checked;
        _lblHoursSuffix.Enabled = _chkPeakAuto.Checked;
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _btnCheck.Enabled = false;
        _btnApply.Enabled = false;
        _btnKeep.Enabled = false;
        _lblStatus.ForeColor = Theme.Colors.Muted;
        _lblStatus.Text = Loc.T("settings.checking");
        Refresh();

        var result = await Task.Run(() => _pricing.Check());

        _result = result;
        _busy = false;
        _btnCheck.Enabled = true;
        _lblChecked.Text = Loc.T("settings.checkedAt", result.CheckedAt.ToString("HH:mm:ss"));

        if (!result.Ok)
        {
            _lblStatus.ForeColor = Theme.Colors.Warning;
            _lblStatus.Text = Loc.T("settings.failed", result.Error)
                              + Environment.NewLine + Loc.T("settings.failedTail");
            return;
        }

        if (result.Differs)
        {
            ShowPending(result);
        }
        else
        {
            _lblStatus.ForeColor = Theme.Colors.Success;
            _lblStatus.Text = Loc.T("settings.same", result.FoundText);
            _btnApply.Enabled = false;
            _btnKeep.Enabled = false;
        }

        RenderPrices(result);
    }

    private void ShowPending(PricingCheckResult result)
    {
        _lblStatus.ForeColor = Theme.Colors.Warning;
        _lblStatus.Text = Loc.T("settings.otherWindows") + Environment.NewLine
                          + Loc.T("settings.nowText", result.CurrentText) + Environment.NewLine
                          + Loc.T("settings.proposed", result.FoundText);
        _btnApply.Enabled = true;
        _btnKeep.Enabled = true;
    }

    private void ApplyPending()
    {
        if (_result == null) return;

        var message = _pricing.Apply(_result);
        Applied = true;
        AppLog.Write(_paths, "тариф: " + message);

        _lblStatus.ForeColor = Theme.Colors.Success;
        _lblStatus.Text = message + Environment.NewLine + Loc.T("settings.appliedTail");
        _btnApply.Enabled = false;
        _btnKeep.Enabled = false;
        RefreshCurrent();
    }

    private void ClearPending(string text)
    {
        _btnApply.Enabled = false;
        _btnKeep.Enabled = false;
        _lblStatus.ForeColor = Theme.Colors.Muted;
        _lblStatus.Text = text;
    }

    private void RenderPrices(PricingCheckResult result)
    {
        if (!result.PricesParsed || result.Prices.Count == 0)
        {
            _txtPrices.Text = Loc.T("prices.empty.unparsed");
            return;
        }

        var text = new StringBuilder();
        if (result.Models.Count > 0)
        {
            text.Append(Loc.T("settings.models", string.Join(", ", result.Models))).AppendLine();
        }

        string currentItem = null;
        foreach (var line in result.Prices)
        {
            if (line.Item != currentItem)
            {
                currentItem = line.Item;
                text.AppendLine(currentItem);
            }

            text.Append("    ").Append(line.Tier.Length > 0 ? line.Tier : Loc.T("prices.none"))
                .Append(": ").AppendLine(string.Join(", ", line.Prices));
        }

        _txtPrices.Text = text.ToString();
    }

    public AppSettings ApplyTo(AppSettings settings)
    {
        settings.Language = LanguageCodes[Math.Max(0, _cmbLanguage.SelectedIndex)];
        settings.ThemeMode = ThemeCodes[Math.Max(0, _cmbTheme.SelectedIndex)];
        settings.ServerWorkingDir = _txtWorkDir.Text.Trim();
        settings.ServerPort = (int)_numPort.Value;
        settings.NodePath = _txtNodePath.Text.Trim();
        settings.DshBinPath = _txtDshPath.Text.Trim();

        settings.BalanceAutoRefresh = _chkAuto.Checked;
        settings.BalanceRefreshMinutes = (int)_numMinutes.Value;
        settings.BalanceWarnEnabled = _chkWarn.Checked;
        settings.BalanceWarnThreshold = _numThreshold.Value;
        settings.PeakAutoCheck = _chkPeakAuto.Checked;
        settings.PeakCheckHours = (int)_numPeakHours.Value;

        NodeLocator.ApplyOverrides(settings.NodePath, settings.DshBinPath);
        return settings;
    }
}
