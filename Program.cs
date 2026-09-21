using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DshTray;

internal enum RunMode
{
    Gui,
    Status,
    SelfTest,
    Icons,
    ServerStart,
    ServerStop,
    ServerRestart,
    Wait,
    PricingCheck,
    Shot,
    Backup,
    BackupCheck,
    Restore,
    UpdateCheck,
    UpdatePrepare,
    NodeCheck,
    InstallNode,
    LangCheck,
    EnvCheck,
    AutostartFix,
    LayoutCheck,
    Help,
}

internal sealed class CliOptions
{
    public RunMode Mode { get; private set; } = RunMode.Gui;
    public int? Port { get; private set; }
    public string OutPath { get; private set; } = "";
    public string IconsPath { get; private set; } = "";
    public string WaitUrl { get; private set; } = "";
    public string FromPath { get; private set; } = "";

    /// <summary>Язык из ключа --lang: важнее настройки и языка системы (для снимков и проверок).</summary>
    public string Language { get; private set; } = "";

    /// <summary>Ключ --onboard: открыть панель и сразу показать мастер первой настройки.</summary>
    public bool Onboard { get; private set; }
    public bool ApplyPricing { get; private set; }
    public bool StartHidden { get; private set; }
    public bool OpenBrowser { get; private set; }

    /// <summary>--full: сделать копию вместе с движком DSH и Node, не меняя настройку.</summary>
    public bool BackupFull { get; private set; }

    /// <summary>--keys: при накате вернуть и ключи с сертификатами (по умолчанию они не трогаются).</summary>
    public bool RestoreKeys { get; private set; }

    /// <summary>Подготовить обновление даже если выпуск не новее: только для проверок.</summary>
    public bool UpdateForce { get; private set; }

    /// <summary>--no-engine: при накате не распаковывать движок DSH и Node.</summary>
    public bool RestoreNoEngine { get; private set; }

    /// <summary>--no-safety: не делать предохранительную копию (так накатывает установщик).</summary>
    public bool RestoreNoSafety { get; private set; }

    /// <summary>--no-settings: не восстанавливать настройки панели (мастер спросит их заново).</summary>
    public bool RestoreNoSettings { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--tray":
                case "--hidden":
                    options.StartHidden = true;
                    break;
                case "--status":
                    options.Mode = RunMode.Status;
                    break;
                case "--selftest":
                    options.Mode = RunMode.SelfTest;
                    break;
                case "--server-start":
                    options.Mode = RunMode.ServerStart;
                    break;
                case "--server-stop":
                    options.Mode = RunMode.ServerStop;
                    break;
                case "--server-restart":
                    options.Mode = RunMode.ServerRestart;
                    break;
                case "--wait":
                    options.Mode = RunMode.Wait;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        options.WaitUrl = args[i + 1];
                        i++;
                    }
                    break;
                case "--pricing-check":
                    options.Mode = RunMode.PricingCheck;
                    break;
                case "--backup":
                    options.Mode = RunMode.Backup;
                    break;
                case "--backup-check":
                    options.Mode = RunMode.BackupCheck;
                    break;
                case "--full":
                    options.BackupFull = true;
                    break;
                case "--restore":
                    options.Mode = RunMode.Restore;
                    break;
                case "--update-check":
                    options.Mode = RunMode.UpdateCheck;
                    break;
                case "--node-check":
                    // Диагностика: какие файлы Node панель соберётся скачать и живы ли адреса.
                    // Появилась после того, как из-за неверно собранного имени архива переносимая
                    // установка Node падала с 404 и Node не ставился вовсе.
                    options.Mode = RunMode.NodeCheck;
                    break;
                case "--update-prepare":
                    // Диагностика: подготовить обновление (скачать, сверить суммы, распаковать),
                    // но не закрывать панель и не запускать сценарий замены.
                    options.Mode = RunMode.UpdatePrepare;
                    break;
                case "--force":
                    options.UpdateForce = true;
                    break;
                case "--install-node":
                    options.Mode = RunMode.InstallNode;
                    break;
                case "--keys":
                    options.RestoreKeys = true;
                    break;
                case "--no-engine":
                    options.RestoreNoEngine = true;
                    break;
                case "--no-safety":
                    options.RestoreNoSafety = true;
                    break;
                case "--no-settings":
                    options.RestoreNoSettings = true;
                    break;
                case "--from":
                    if (i + 1 < args.Length) { options.FromPath = args[i + 1]; i++; }
                    break;
                case "--apply":
                    options.ApplyPricing = true;
                    break;
                case "--open":
                    options.OpenBrowser = true;
                    break;
                case "--shot":
                    options.Mode = RunMode.Shot;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        options.IconsPath = args[i + 1];
                        i++;
                    }
                    break;
                case "--icons":
                    options.Mode = RunMode.Icons;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        options.IconsPath = args[i + 1];
                        i++;
                    }
                    break;
                case "--lang-check":
                    options.Mode = RunMode.LangCheck;
                    break;
                case "--env-check":
                    options.Mode = RunMode.EnvCheck;
                    break;
                case "--autostart-fix":
                    options.Mode = RunMode.AutostartFix;
                    break;
                case "--layout-check":
                    options.Mode = RunMode.LayoutCheck;
                    break;
                case "--onboard":
                    options.Onboard = true;
                    break;
                case "--lang":
                    if (i + 1 < args.Length) { options.Language = args[i + 1]; i++; }
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options.Mode = RunMode.Help;
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port)) options.Port = port;
                    i++;
                    break;
                case "--out":
                    if (i + 1 < args.Length) options.OutPath = args[i + 1];
                    i++;
                    break;
            }
        }
        return options;
    }
}

internal static class Program
{
    /// <summary>
    /// Имена мьютекса и события «покажи окно». По умолчанию одни на всю машину: у панели
    /// один экземпляр на сеанс. Переменная DSH_PANEL_INSTANCE меняет их для проверок —
    /// так проверка может поднять свою панель рядом с работающей, не мешая ей.
    /// </summary>
    private static string InstanceName()
    {
        var custom = Environment.GetEnvironmentVariable("DSH_PANEL_INSTANCE");
        return string.IsNullOrWhiteSpace(custom) ? @"Local\DshTray" : custom.Trim();
    }

    private static string ShowEventName => InstanceName() + ".ShowPanel";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [STAThread]
    private static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);

        // Настройки прежних версий лежали в %APPDATA%\DeepSeekHarness — переносим один раз,
        // копированием: своя папка продукта и прежняя остаются на месте.
        AppPaths.MigrateLegacyData();

        // Язык интерфейса: ключ --lang важнее настройки, настройка важнее языка системы.
        var paths = new AppPaths(AppContext.BaseDirectory);
        var settings = AppSettings.Load(paths.SettingsPath, paths.LegacySettingsPath);
        NodeLocator.ApplyOverrides(settings.NodePath, settings.DshBinPath);
        Loc.Init(string.IsNullOrWhiteSpace(options.Language) ? settings.Language : options.Language);

        // Тема — до первого окна и до хоста трея: палитра и роли шрифтов читаются из Theme
        // в момент построения окна, и решать это после значило бы показать человеку светлое
        // окно при тёмной настройке. Источник один — настройка «theme» (ключ «auto» читает
        // системную тему Windows внутри Theme.Use).
        Theme.Use(Theme.ModeFrom(settings.ThemeMode));

        if (options.Mode == RunMode.Help)
        {
            WriteReport(HelpText(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.LangCheck)
        {
            WriteReport(LanguageCheckReport(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.EnvCheck)
        {
            WriteReport(EnvironmentReport(settings, paths), options.OutPath);
            return 0;
        }

        // Починка автозапуска по команде — тот же код, что панель выполняет при старте.
        // Нужна проверкам (они уводят реестр в свою ветку через DSH_PANEL_RUN_KEY) и человеку
        // для случая «панель не поднимается при входе, а в трее старая копия».
        if (options.Mode == RunMode.AutostartFix)
        {
            WriteReport(AutostartFixReport(), options.OutPath);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        if (options.Mode == RunMode.Icons)
        {
            var path = !string.IsNullOrWhiteSpace(options.IconsPath)
                ? options.IconsPath
                : !string.IsNullOrWhiteSpace(options.OutPath)
                    ? options.OutPath
                    : Path.Combine(AppContext.BaseDirectory, "preview", "tray-icons.png");

            WriteIconPreview(path);
            WriteReport($"Значки состояний: {path}", "");
            return 0;
        }

        if (options.Mode is RunMode.Status or RunMode.SelfTest
            or RunMode.ServerStart or RunMode.ServerStop or RunMode.ServerRestart
            or RunMode.Shot or RunMode.Wait or RunMode.PricingCheck or RunMode.LayoutCheck
            or RunMode.Backup or RunMode.BackupCheck or RunMode.Restore or RunMode.UpdateCheck
            or RunMode.UpdatePrepare or RunMode.NodeCheck
            or RunMode.InstallNode)
        {
            return RunCli(options);
        }

        return RunGui(options);
    }

    private static int RunGui(CliOptions options)
    {
        using var mutex = new Mutex(true, InstanceName() + ".SingleInstance", out var created);
        if (!created)
        {
            // Уже запущено — просим первый экземпляр показать окно и уходим.
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // Первый экземпляр не отвечает — просто выходим.
            }
            return 0;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        // --onboard (так панель запускает установщик): мастер первой настройки. Решение
        // «показывать ли мастер» принимает сам TrayHost в одном месте — раньше мастер
        // открывался и здесь, и ещё раз таймером старта, и на первом запуске человек
        // получал два окна мастера друг на друге (модальное окно не мешает таймеру).
        // Окно панели до мастера не показываем: он должен открыться на чистом экране.
        using var host = new TrayHost(options.StartHidden || options.Onboard, showEvent, options.Port,
            autoStart: true, onboard: options.Onboard);

        Application.Run(host);
        GC.KeepAlive(mutex);
        return 0;
    }

    private static int RunCli(CliOptions options)
    {
        var paths = new AppPaths(AppContext.BaseDirectory);
        var settings = AppSettings.Load(paths.SettingsPath, paths.LegacySettingsPath);
        NodeLocator.ApplyOverrides(settings.NodePath, settings.DshBinPath);

        // Порт: ключ командной строки важнее настройки, настройка — значения по умолчанию.
        var port = options.Port ?? settings.ServerPort;

        var server = new ServerController(paths, port) { WorkingDirectory = settings.ServerWorkingDir };
        var peak = new PeakService(paths.PricingPath);
        var balance = new BalanceService(paths);

        var report = new StringBuilder();

        if (options.Mode == RunMode.PricingCheck)
        {
            var pricing = new PricingUpdateService(paths);
            var check = string.IsNullOrWhiteSpace(options.FromPath)
                ? pricing.Check()
                : PricingUpdateService.Parse(File.ReadAllText(options.FromPath), paths);

            report.AppendLine(check.Ok ? "страница разобрана" : "разобрать не удалось: " + check.Error);

            // Подробности разбора — в отчёт, а не в окно: в окне они только пугают длиной.
            if (!check.Ok && check.Diag.Length > 0) report.AppendLine("  " + check.Diag);

            if (check.Ok)
            {
                report.AppendLine("сейчас: " + check.CurrentText);
                report.AppendLine("на странице: " + check.FoundText);
                report.AppendLine("окна " + (check.Differs ? "ОТЛИЧАЮТСЯ" : "совпадают"));

                if (check.PricesParsed)
                {
                    report.AppendLine($"цены (модели: {string.Join(", ", check.Models)}):");
                    foreach (var line in check.Prices)
                    {
                        report.AppendLine($"  {line.Item} · {line.Tier}: {string.Join(", ", line.Prices)}");
                    }
                }
                else
                {
                    report.AppendLine("таблицу цен разобрать не удалось");
                }

                if (options.ApplyPricing && check.Differs)
                {
                    report.AppendLine(pricing.Apply(check));
                }
            }

            WriteReport(report.ToString(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.Backup)
        {
            // --full делает тяжёлую копию разово, не меняя настройку пользователя.
            var backups = new BackupService(paths, settings, options.BackupFull ? true : null);
            report.AppendLine("Папка копий: " + backups.Folder);
            report.AppendLine(Loc.T("cli.composition",
                Loc.T((options.BackupFull || settings.BackupWithEngine) ? "cli.compositionEngine" : "cli.compositionThin")
                + (settings.BackupWithSessions ? Loc.T("cli.compositionSessions") : "")));

            var created = backups.Create();
            report.AppendLine(created.Summary());
            if (created.Ok) report.AppendLine(Loc.T("backup.file", created.Path));

            AppLog.Write(paths, "резервная копия: " + created.Summary());

            var list = backups.List();
            report.AppendLine(Loc.T("cli.backupsInFolder", list.Count, settings.BackupKeepCount));
            foreach (var item in list.Take(10)) report.AppendLine("  " + item.Describe());

            WriteReport(report.ToString(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.BackupCheck)
        {
            var service = new BackupService(paths, settings);
            var target = options.FromPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = service.List().FirstOrDefault()?.Path;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                report.AppendLine(Loc.T("cli.backupsNotFound", service.Folder));
            }
            else
            {
                report.AppendLine(Loc.T("cli.checking", target));
                var check = BackupService.Verify(target);
                report.AppendLine(check.Summary());
                report.AppendLine(Loc.T(check.Ok ? "cli.valid" : "cli.invalid"));
            }

            WriteReport(report.ToString(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.Restore)
        {
            var service = new BackupService(paths, settings);
            var target = options.FromPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = service.List().FirstOrDefault()?.Path;
                report.AppendLine(Loc.T("restore.newest") + ": " + (target ?? Loc.T("common.dash")));
            }

            var plan = RestoreService.Plan(target ?? "");
            if (!plan.Ok)
            {
                report.AppendLine(Loc.T("restore.planFailed", plan.Error));
                WriteReport(report.ToString(), options.OutPath);
                return 1;
            }

            report.AppendLine(Loc.T("cli.restorePlan", plan.Origin()));
            foreach (var line in plan.Contents()) report.AppendLine("  " + line);
            if (plan.HasKeys) report.AppendLine("  " + Loc.T("cli.restoreKeys", plan.Keys.Count));

            // Накат идёт по живым файлам DSH: пока сервер работает, часть из них занята.
            if (server.GetStatus().Running)
            {
                report.AppendLine(Loc.T("cli.restoreStopping"));
                server.Stop();
            }

            var restoreOptions = new RestoreOptions
            {
                Keys = options.RestoreKeys,
                Engine = !options.RestoreNoEngine,
                SafetyCopy = !options.RestoreNoSafety,
                PanelSettings = !options.RestoreNoSettings,
            };

            report.AppendLine(Loc.T("cli.restoreRun") + "…");
            var restored = RestoreService.Restore(plan, restoreOptions, paths, settings, line => report.AppendLine("  " + line));
            report.AppendLine(restored.Summary());
            foreach (var line in restored.Restored) report.AppendLine("  " + Loc.T("cli.restoreRestored") + " " + line);
            foreach (var line in restored.Skipped) report.AppendLine("  " + Loc.T("cli.restoreSkipped") + " " + line);

            AppLog.Write(paths, "накат копии: " + restored.Summary());

            WriteReport(report.ToString(), options.OutPath);
            return restored.Ok ? 0 : 1;
        }

        if (options.Mode == RunMode.UpdateCheck)
        {
            report.AppendLine(Loc.T("cli.updateRepo", UpdateService.Repository));
            report.AppendLine(Loc.T("cli.updateCurrent", AppVersion.Short));

            var update = UpdateService.Check();
            report.AppendLine(update.Summary());

            if (update.Ok)
            {
                if (update.Name.Length > 0) report.AppendLine("  " + update.Name);
                if (update.PageUrl.Length > 0) report.AppendLine("  " + update.PageUrl);
                if (update.ZipUrl.Length > 0) report.AppendLine("  " + Loc.T("cli.updateAsset", update.AssetName));
                if (update.SetupUrl.Length > 0) report.AppendLine("  " + Loc.T("cli.updateAsset", "dsh-panel-setup.exe"));
                if (update.Notes.Length > 0)
                {
                    report.AppendLine(Loc.T("update.notes"));

                    // Тело релиза несёт три блока на трёх языках с машинными метками (см.
                    // UpdateService.PickNotes). В консольном отчёте показываем блок на языке
                    // отчёта (ключ --lang): метки и чужие языки там только мешают.
                    var notes = UpdateService.PickNotes(update.Notes, Loc.Language);
                    foreach (var line in notes.Replace("\r", "").Split('\n').Take(30)) report.AppendLine("  " + line);
                }
            }
            else if (update.Error.Length > 0)
            {
                report.AppendLine("  " + update.Error);
            }

            // Результат запоминаем: окно обновлений показывает его без обращения к сети.
            settings.UpdateCheckedAt = DateTime.Now;
            settings.UpdateLatest = update.Latest;
            settings.UpdatePublished = update.PublishedAt == default ? "" : update.PublishedAt.ToString("yyyy-MM-dd");
            settings.UpdatePageUrl = update.PageUrl;

            // Тело выпуска — на случай смены языка интерфейса (заметки пересобираются в окне),
            // выбранный блок — для показа прямо сейчас. Обрезка у обоих своя; тело длиннее
            // предела хранится уже разобранным на блоки (см. UpdateService.RawNotes).
            settings.UpdateNotesRaw = UpdateService.RawNotes(update.Notes);
            settings.UpdateNotes = UpdateService.PickNotesForPanel(settings.UpdateNotesRaw, Loc.Language);
            settings.Save(paths.SettingsPath);

            WriteReport(report.ToString(), options.OutPath);
            return update.Ok ? 0 : 1;
        }

        if (options.Mode == RunMode.UpdatePrepare)
        {
            // Диагностический режим: пройти весь путь обновления — поиск выпуска, скачивание,
            // сверку контрольных сумм, распаковку и подготовку сценария замены. Нужен, чтобы
            // проверять целостность обновления без публикации: с DSH_PANEL_API проверка
            // поднимает локальную заглушку GitHub и прогоняет тот же код.
            // Панель при этом НЕ закрывается и сценарий НЕ запускается: только подготовка.
            report.AppendLine(Loc.T("cli.updateRepo", UpdateService.Repository));
            report.AppendLine(Loc.T("cli.updateCurrent", AppVersion.Short));

            var check = UpdateService.Check();
            report.AppendLine(check.Summary());
            if (!check.Ok)
            {
                report.AppendLine("  " + check.Error);
                WriteReport(report.ToString(), options.OutPath);
                return 1;
            }

            if (check.SumsUrl.Length == 0) report.AppendLine("  " + Loc.T("cli.updateNoSumsAsset"));

            var prepared = UpdateService.Prepare(check, paths, message => report.AppendLine("  " + message), ignoreNewer: options.UpdateForce);
            report.AppendLine(prepared.Ok
                ? Loc.T("cli.updatePrepared", prepared.Version, prepared.StagedFolder)
                : Loc.T("cli.updatePrepareFailed", prepared.Error));

            // Печатаем готовую команду замены: по ней видно, что именно выполнит панель, и её
            // можно запустить руками — так проверяют путь обновления там, где он сорвался.
            if (prepared.Ok && prepared.CommandLine.Length > 0)
            {
                report.AppendLine("  " + Loc.T("cli.updateCommand") + ":");
                report.AppendLine("  " + prepared.CommandLine);
            }

            WriteReport(report.ToString(), options.OutPath);
            return prepared.Ok ? 0 : 1;
        }

        if (options.Mode == RunMode.NodeCheck)
        {
            // Диагностика: какие файлы Node панель соберётся скачать и отвечают ли эти адреса.
            // Имена файлов у Node разные (у MSI без win-, у архива с win-), и однажды это
            // угадывание закончилось 404: Node не ставился ни из мастера, ни из установщика.
            report.AppendLine(Loc.T("cli.updateRepo", UpdateService.Repository));
            try
            {
                var release = NodeInstaller.FindLatestLts();
                report.AppendLine(Loc.T("cli.nodeLatest", release.Version));

                var urls = new List<string> { release.ZipUrl };
                if (release.MsiUrl.Length > 0) urls.Add(release.MsiUrl);

                var bad = 0;
                foreach (var url in urls)
                {
                    var probeCode = Probe(url);
                    report.AppendLine(Loc.T("cli.nodeUrl", url, probeCode));
                    if (probeCode != "200") bad++;
                }

                WriteReport(report.ToString(), options.OutPath);
                return bad == 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                report.AppendLine(Loc.T("cli.nodeCheckFailed", error.Message));
                WriteReport(report.ToString(), options.OutPath);
                return 1;
            }
        }
        if (options.Mode == RunMode.InstallNode)
        {
            // Этим ключом пользуется установщик: он ставит Node до первого запуска панели,
            // чтобы мастеру осталось поставить только движок DSH.
            report.AppendLine(Loc.T("cli.nodeInstall"));
            var nodeResult = NodeInstaller.Install(paths, settings, step => report.AppendLine("  " + step));
            report.AppendLine(nodeResult.Summary());
            AppLog.Write(paths, "Node (по просьбе установщика): " + nodeResult.Summary());

            WriteReport(report.ToString(), options.OutPath);
            return nodeResult.Ok ? 0 : 1;
        }

        if (options.Mode == RunMode.Wait)
        {
            var started = Environment.TickCount64;

            if (string.IsNullOrWhiteSpace(options.WaitUrl))
            {
                // Без ссылки: ждём, пока начнёт отвечать любая из известных.
                var found = server.WaitForWorkingUrl(60);
                report.AppendLine(found.Length > 0
                    ? $"рабочая ссылка найдена через {Environment.TickCount64 - started} мс (из {found.Length} символов)"
                    : "рабочая ссылка не найдена за 60 с");
            }
            else
            {
                var ready = server.WaitUntilServing(options.WaitUrl, 60);
                report.AppendLine(ready
                    ? $"страница отвечает через {Environment.TickCount64 - started} мс"
                    : "страница не ответила за 60 с");
            }

            report.AppendLine($"известных ссылок: {server.CandidateUrls().Count}");
            report.AppendLine($"последний ответ: {server.LastProbeResult}");
            WriteReport(report.ToString(), options.OutPath);
            return 0;
        }

        if (options.Mode is RunMode.ServerStart or RunMode.ServerStop or RunMode.ServerRestart)
        {
            try
            {
                switch (options.Mode)
                {
                    case RunMode.ServerStart:
                        server.Start(options.OpenBrowser);
                        report.AppendLine(Loc.T("cli.serverStarted", port));
                        break;
                    case RunMode.ServerRestart:
                        server.Restart(options.OpenBrowser);
                        report.AppendLine(Loc.T("cli.serverRestarted", port));
                        break;
                    default:
                        {
                            // Порт может держать чужая программа: тогда Stop ничего не делает
                            // и объясняет почему — панель не имеет права её останавливать.
                            var stopStatus = server.Stop();
                            report.AppendLine(stopStatus.Refusal.Length > 0
                                ? stopStatus.Refusal
                                : Loc.T("cli.serverStopped", port));
                            break;
                        }
                }
            }
            catch (Exception error)
            {
                report.AppendLine(Loc.T("cli.commandFailed", error.Message));
            }
        }

        var status = server.GetStatus();
        var peakState = peak.GetState();

        report.AppendLine(Loc.T("cli.port", port));
        report.AppendLine(Loc.T("cli.server", status.StateText
                          + (status.Pid > 0 ? Loc.T("cli.pid", status.Pid, status.ProcessName) : "")));
        report.AppendLine(Loc.T("cli.urlKnown",
            Loc.T(server.GetAuthenticatedUrl(skipRunningCheck: true).Length > 0 ? "common.yes" : "common.no")));
        report.AppendLine(Loc.T("tray.headerTariff", peakState.StateText));
        report.AppendLine(Loc.T("cli.nextSwitch",
            string.IsNullOrEmpty(peakState.NextText) ? Loc.T("common.dash") : peakState.NextText));
        report.AppendLine(Loc.T("main.windows", peakState.WindowText, peakState.Checked));
        report.AppendLine(Loc.T("cli.autostart", DescribeAutostart(Autostart.Inspect())));
        report.AppendLine(Loc.T("cli.openBrowser", Loc.T(settings.OpenBrowserOnStart ? "common.yes" : "common.no")));

        var balanceResult = balance.Query();
        if (balanceResult.Ok)
        {
            report.AppendLine(Loc.T("cli.balance", balanceResult.Summary
                              + (string.IsNullOrEmpty(balanceResult.Detail) ? "" : $" ({balanceResult.Detail})")));
            report.AppendLine(Loc.T("cli.balanceAvailable",
                Loc.T(balanceResult.Available ? "common.yes" : "common.no"), balanceResult.Source));
        }
        else
        {
            report.AppendLine(Loc.T("cli.balanceFailed", balanceResult.Error));
        }

        // Резервные копии: состояние, папка и что там уже лежит.
        var backupService = new BackupService(paths, settings);
        var backupList = backupService.List();
        report.AppendLine(settings.BackupEnabled
            ? Loc.T("cli.backupOn", settings.BackupIntervalHours, settings.BackupKeepCount)
              + (settings.BackupWithEngine ? Loc.T("cli.backupEngine") : "")
            : Loc.T("cli.backupOff"));
        report.AppendLine(Loc.T("cli.backupFolder", backupService.Folder, backupList.Count)
                          + (backupList.Count > 0 ? Loc.T("cli.backupLast", backupList[0].Describe()) : ""));

        if (options.Mode == RunMode.LayoutCheck)
        {
            PricingCheckResult sampleForLayout = null;
            try
            {
                var samplePath = Path.Combine(AppContext.BaseDirectory, "preview", "pricing.html");
                if (File.Exists(samplePath)) sampleForLayout = PricingUpdateService.Parse(File.ReadAllText(samplePath), paths);
            }
            catch
            {
                // Без образца окно цен покажет пустое состояние — этого достаточно.
            }

            using var showEventLayout = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var hostLayout = new TrayHost(startHidden: true, showEvent: showEventLayout, port: options.Port, autoStart: false);

            var issues = hostLayout.LayoutIssues(new PricingUpdateService(paths), sampleForLayout);
            report.AppendLine(issues.Count == 0
                ? "вёрстка чистая: текст нигде не обрезан"
                : "обрезанный текст (" + issues.Count + "): " + string.Join("; ", issues));
            foreach (var issue in issues) report.AppendLine("  " + issue);

            WriteReport(report.ToString(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.Shot)
        {
            var path = !string.IsNullOrWhiteSpace(options.IconsPath)
                ? options.IconsPath
                : Path.Combine(AppContext.BaseDirectory, "preview", "panel.png");

            var balanceShot = balanceResult;
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var host = new TrayHost(startHidden: true, showEvent: showEvent, port: options.Port, autoStart: false);
            host.SavePanelShot(path, balanceShot);

            // Рядом со снимком панели — снимки окна настроек и окна цен.
            var directory = Path.GetDirectoryName(path) ?? ".";
            var settingsPath = Path.Combine(directory, "settings.png");
            var settingsBigPath = Path.Combine(directory, "settings-big.png");
            var pricesPath = Path.Combine(directory, "prices.png");
            host.SaveSettingsShot(settingsPath);
            host.SaveSettingsShot(settingsBigPath, stretched: true);

            // Для снимка цен берём сохранённую копию страницы, если она рядом.
            PricingCheckResult sample = null;
            try
            {
                var samplePath = Path.Combine(directory, "pricing.html");
                if (File.Exists(samplePath)) sample = PricingUpdateService.Parse(File.ReadAllText(samplePath), paths);
            }
            catch
            {
                // Без образца снимок покажет пустое состояние.
            }

            host.SavePricesShot(pricesPath, sample);
            host.SaveBackupsShot(Path.Combine(directory, "backups.png"));
            host.SaveRestoreShot(Path.Combine(directory, "restore.png"));
            host.SaveOnboardingShot(Path.Combine(directory, "onboarding.png"));

            // Второй шаг мастера — тот, где ставят Node и движок: он самый важный для новичка.
            host.SaveOnboardingShot(Path.Combine(directory, "onboarding-env.png"), step: 1);
            host.SaveMenuShot(Path.Combine(directory, "menu.png"));

            report.AppendLine($"Снимок окна: {path}");
            report.AppendLine($"Снимок настроек: {settingsPath}");
            report.AppendLine($"Снимок настроек (растянуто): {settingsBigPath}");
            report.AppendLine($"Снимок цен: {pricesPath}");
            report.AppendLine($"Снимок окна копий: {Path.Combine(directory, "backups.png")}");
            report.AppendLine($"Снимок меню трея: {Path.Combine(directory, "menu.png")}");
            WriteReport(report.ToString(), options.OutPath);
            return 0;
        }

        if (options.Mode == RunMode.SelfTest)
        {
            var problems = 0;

            // Версии: предрелиз старше финального — иначе человек, поставивший rc, никогда
            // не получил бы выпуск (апдейтер считал бы их одной версией). Проверяем таблицей.
            var versions = new (string Candidate, string Current, bool Newer)[]
            {
                ("1.21.0", "1.20.9", true),
                ("1.20.0", "1.20.0", false),
                ("1.20.0", "1.20.0-rc.1", true),
                ("1.20.0-rc.1", "1.20.0", false),
                ("1.20.0-rc.10", "1.20.0-rc.2", true),
                ("1.20.0-beta", "1.20.0-rc.1", false),
                ("v1.20.1", "1.20.0", true),
                ("1.9.0", "1.10.0", false),
                // .NET дописывает в ProductVersion хеш сборки («1.20.2+8529c83»): без обрезки
                // суффикса сверка версий валила бы каждое обновление, а пересборка под тем же
                // номером выглядела бы новее. Обе стороны сравнения обязаны чиститься.
                ("1.20.2+8529c83", "1.20.2", false),
                ("1.20.2", "1.20.2+8529c83", false),
                ("1.20.3+8529c83", "1.20.2+deadbee", true),
            };

            foreach (var item in versions)
            {
                var actual = UpdateService.IsNewer(item.Candidate, item.Current);
                var mark = actual == item.Newer ? "ок" : "БЕДА";
                if (actual != item.Newer) problems++;
                report.AppendLine($"Версии {mark}: {item.Candidate} против {item.Current} — " +
                                  (actual ? "новее" : "не новее"));
            }

            // Разбор файла контрольных сумм: строки как у sha256sum, с комментарием, мусором
            // без хэша, звёздочкой и путём вместо имени файла.
            var sums = UpdateService.ParseSums(new[]
            {
                "# контрольные суммы выпуска",
                "",
                "d41d8cd98f00b204e9800998ecf8427e  не-хэш",
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  DshPanel.zip",
                "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 *dsh-panel-setup.exe",
                "2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae  some/path/DshPanel-selfcontained.zip",
            });

            var sumsOk = sums.Count == 3
                         && sums.ContainsKey("DshPanel.zip")
                         && sums.ContainsKey("dsh-panel-setup.exe")
                         && sums.ContainsKey("DshPanel-selfcontained.zip")
                         && !sums.ContainsKey("не-хэш");
            if (!sumsOk) problems++;
            report.AppendLine($"Контрольные суммы {(sumsOk ? "ок" : "БЕДА")}: разобрано {sums.Count} из 3 записей");

            // Заметки к выпуску на языке интерфейса. Тело релиза несёт три блока с машинными
            // маркерами; таблица кейсов проверяет выбор блока и — отдельно — что старые выпуски
            // без маркеров вовсе (до 1.21.0) показываются целиком, как раньше. Пропустить этот
            // случай нельзя: иначе человек с выпуском 1.20.3 увидел бы пустое окно «Что нового».
            //
            // Форма тел — как у настоящего тела 1.21.0 после tools\release-notes.ps1: заголовок
            // языка обычным текстом, затем пара машинных маркеров. HTML-тегов в теле нет — панели
            // прежних версий показывают тело как есть и увидели бы их.
            var notesBody1 = "## English\n\n<!-- dsh-notes:en -->\nONE en\n<!-- /dsh-notes:en -->\n\n" +
                             "## Русский\n\n<!-- dsh-notes:ru -->\nОДИН ru\n<!-- /dsh-notes:ru -->\n\n" +
                             "## 中文\n\n<!-- dsh-notes:zh -->\n一 zh\n<!-- /dsh-notes:zh -->";
            var notesBody2 = "## English\n\n<!-- dsh-notes:en -->\nTWO en\n<!-- /dsh-notes:en -->\n\n" +
                             "## 中文\n\n<!-- dsh-notes:zh -->\n二 zh\n<!-- /dsh-notes:zh -->";
            var notesBody3 = "## English\n\n<!-- dsh-notes:en -->\nTHREE en\n<!-- /dsh-notes:en -->\n\n" +
                             "## Русский\n\n<!-- dsh-notes:ru -->\nТРИ ru\n<!-- /dsh-notes:ru -->";
            // Тело без маркеров — так выглядят ВСЕ выпуски до 1.21.0: показывается целиком,
            // как показывала панель раньше. Синтетическая строка, а не тело 1.20.3: важно
            // структурное свойство «маркеров нет».
            var notesBodyOld = "### Исправлено\n\n- **Ссылка на панель** — правилась вручную.";
            // Битые маркеры: закрывающий без открывающего, затем открывающий без закрывающего.
            var notesBodyBroken = "<!-- /dsh-notes:en -->\nтекст до\n<!-- dsh-notes:en -->\nхвост без закрытия";
            // Метка ПОСРЕДИ строки: панель такую границей не считает (и release-notes.ps1 так не
            // пишет). Здесь есть и парный маркер в неверном месте — внутри той же строки, где уже
            // идёт текст. Кейс настоящий: если разбор начнёт опознавать метку в любом месте строки,
            // строка «текст <!-- dsh-notes:ru --> внутри» откроет блок ru — и вернётся не тело.
            var notesBodyInline = "текст <!-- dsh-notes:en --> внутри\nСТРОКА\n<!-- /dsh-notes:en -->\nru-текст";
            // Пример метки внутри ограждённого блока кода. Тело — недоверенный текст: такую
            // историю можно написать руками на странице выпуска. Разбор обязан пропустить блок
            // кода целиком, иначе объявит пример блоком ru и отбросит настоящий перевод.
            var notesBodyFenced = "<!-- dsh-notes:en -->\nEN вступление\n\n```\n<!-- dsh-notes:ru -->\nПРИМЕР из блока кода\n<!-- /dsh-notes:ru -->\n```\n\nEN хвост\n<!-- /dsh-notes:en -->\n\n" +
                                  "<!-- dsh-notes:ru -->\nНАСТОЯЩИЙ ru\n<!-- /dsh-notes:ru -->";
            // Инвариант «обрезка ПОСЛЕ выбора блока»: английский блок длиннее предела показа,
            // русский — короче. Русский обязан выжить и остаться целым: если обрезать тело до
            // выбора, длинный английский вытеснил бы короткий перевод и человек прочитал бы
            // чужой язык (или пустоту).
            var notesBodyLong = "<!-- dsh-notes:en -->\n" + new string('A', 9000) +
                                "\n<!-- /dsh-notes:en -->\n\n<!-- dsh-notes:ru -->\nКОРОТКИЙ ru\n<!-- /dsh-notes:ru -->";

            var notesCases = new (string Name, string Body, string Language, string Expected)[]
            {
                ("все три блока — по языку", notesBody1, "en", "ONE en"),
                ("все три блока — по языку", notesBody1, "ru", "ОДИН ru"),
                ("все три блока — по языку", notesBody1, "zh", "一 zh"),
                ("нет ru — английский", notesBody2, "ru", "TWO en"),
                ("нет zh — английский", notesBody3, "zh", "THREE en"),
                ("маркеров нет — тело целиком", notesBodyOld, "en", notesBodyOld),
                ("маркеров нет — тело целиком", notesBodyOld, "zh", notesBodyOld),
                ("битые маркеры — тело целиком", notesBodyBroken, "en", notesBodyBroken),
                ("маркер посреди строки — тело целиком", notesBodyInline, "en", notesBodyInline),
                ("метка в блоке кода — не граница", notesBodyFenced, "ru", "НАСТОЯЩИЙ ru"),
                ("метка в блоке кода — английский цел", notesBodyFenced, "en", "EN вступление\n\n```\n<!-- dsh-notes:ru -->\nПРИМЕР из блока кода\n<!-- /dsh-notes:ru -->\n```\n\nEN хвост"),
            };

            foreach (var item in notesCases)
            {
                var picked = UpdateService.PickNotes(item.Body, item.Language);
                var ok = string.Equals(picked, item.Expected, StringComparison.Ordinal);
                if (!ok) problems++;
                report.AppendLine($"Заметки {(ok ? "ок" : "БЕДА")}: {item.Name}, язык {item.Language} — " +
                                  (ok ? "выбрано верно" : $"выбрано «{picked}»"));
            }

            // Отдельно — предел показа: он режет УЖЕ ВЫБРАННЫЙ блок, а не тело. Тело здесь
            // длиннее 8000, выбранный блок — короче, поэтому предел не должен его тронуть.
            var longPicked = UpdateService.PickNotesForPanel(notesBodyLong, "ru");
            var longOk = string.Equals(longPicked, "КОРОТКИЙ ru", StringComparison.Ordinal);
            if (!longOk) problems++;
            report.AppendLine($"Заметки {(longOk ? "ок" : "БЕДА")}: обрезка после выбора блока (тело {notesBodyLong.Length} знаков, " +
                              $"выбранный блок {longPicked.Length})");

            // И тот же предел наоборот: выбранный блок длиннее предела — обрезан ровно по нему.
            var cutPicked = UpdateService.PickNotesForPanel("<!-- dsh-notes:ru -->\n" + new string('Я', 9000) +
                                                            "\n<!-- /dsh-notes:ru -->", "ru");
            var cutOk = cutPicked.Length == 8000;
            if (!cutOk) problems++;
            report.AppendLine($"Заметки {(cutOk ? "ок" : "БЕДА")}: выбранный блок обрезан по пределу показа ({cutPicked.Length} знаков)");

            // Сырое тело — то, что кладётся в настройки: оно тоже под пределом, но своим.
            // Первый случай — тело без маркеров (так выглядят выпуски до 1.21.0): оно обязано
            // остаться собой, только обрезанным, и PickNotes вернёт его целиком, как раньше.
            // Второй — та же обрезка ровно на границе суррогатной пары: разорванная пара уехала
            // бы в settings.json как «\uFFFD», поэтому разрез отступает на знак назад.
            var rawBody = UpdateService.RawNotes(new string('R', 25000));
            var rawOk = rawBody.Length == 20000 &&
                        string.Equals(UpdateService.PickNotes(rawBody, "ru"), rawBody, StringComparison.Ordinal);
            if (!rawOk) problems++;
            report.AppendLine($"Заметки {(rawOk ? "ок" : "БЕДА")}: сырое тело без маркеров обрезано по своему пределу и осталось целым ({rawBody.Length} знаков)");

            var emojiBody = new string('R', 19999) + char.ConvertFromUtf32(0x1F600) + new string('y', 274);
            var rawEmoji = UpdateService.RawNotes(emojiBody);
            var emojiOk = rawEmoji.Length <= 20000 && !HasLonelySurrogate(rawEmoji);
            if (!emojiOk) problems++;
            report.AppendLine($"Заметки {(emojiOk ? "ок" : "БЕДА")}: предельная обрезка не разорвала суррогатную пару ({rawEmoji.Length} знаков)");

            // Тело длиннее предела СЫРОГО тела, и закрывающий маркер английского блока остался
            // за ним. Разбор идёт до обрезки, поэтому у выбора языка есть блок — человек читает
            // свой перевод, а не всё английское тело с обрывком маркера.
            var overLimitBody = "<!-- dsh-notes:en -->\n" + new string('A', 21000) +
                                "\n<!-- /dsh-notes:en -->\n\n<!-- dsh-notes:ru -->\nНАСТОЯЩИЙ РУССКИЙ ТЕКСТ\n<!-- /dsh-notes:ru -->";
            var rawOver = UpdateService.RawNotes(overLimitBody);
            var overPicked = UpdateService.PickNotes(rawOver, "ru");
            var overOk = rawOver.Length <= 20000 &&
                         string.Equals(overPicked, "НАСТОЯЩИЙ РУССКИЙ ТЕКСТ", StringComparison.Ordinal) &&
                         !HasLonelySurrogate(rawOver);
            if (!overOk) problems++;
            report.AppendLine($"Заметки {(overOk ? "ок" : "БЕДА")}: тело длиннее предела — блок выбран после разбора (тело {rawOver.Length} знаков, выбран {overPicked.Length})");

            // Ограждение кода, через которое проходит срез блока. Разрезанное пополам, оно
            // уводит закрывающий маркер «в код», при повторном разборе не находится ни одного
            // блока — и русский человек читает английское тело (находка перепроверки). Кейс
            // проверяет и предел, и то, что перевод остался доступен.
            var notesBodyFencedLong = "<!-- dsh-notes:en -->\nintro\n```\n" + new string('B', 20000) +
                                      "\n```\nend\n<!-- /dsh-notes:en -->\n\n<!-- dsh-notes:ru -->\nНАСТОЯЩИЙ РУССКИЙ ТЕКСТ\n<!-- /dsh-notes:ru -->";
            var fencedRaw = UpdateService.RawNotes(notesBodyFencedLong);
            var fencedPicked = UpdateService.PickNotes(fencedRaw, "ru");
            var fencedOk = fencedRaw.Length <= 20000 &&
                           string.Equals(fencedPicked, "НАСТОЯЩИЙ РУССКИЙ ТЕКСТ", StringComparison.Ordinal);
            if (!fencedOk) problems++;
            report.AppendLine($"Заметки {(fencedOk ? "ок" : "БЕДА")}: ограждение кода пережило срез — перевод на месте (тело {fencedRaw.Length} знаков, выбран {fencedPicked.Length})");

            // Ограждение внутри САМОГО перевода: русский обязан прочитать русское начало, а не
            // английский текст. Хвост за ограждением отбрасывается — резать ограждение нельзя.
            var notesBodyFencedRu = "<!-- dsh-notes:en -->\nENGLISH TEXT\n<!-- /dsh-notes:en -->\n\n" +
                                    "<!-- dsh-notes:ru -->\nРУССКИЙ НАЧАЛО\n```\n" + new string('Р', 20000) +
                                    "\n```\nРУССКИЙ ХВОСТ\n<!-- /dsh-notes:ru -->";
            var fencedRuRaw = UpdateService.RawNotes(notesBodyFencedRu);
            var fencedRuPicked = UpdateService.PickNotes(fencedRuRaw, "ru");
            var fencedRuOk = fencedRuRaw.Length <= 20000 &&
                             fencedRuPicked.StartsWith("РУССКИЙ НАЧАЛО", StringComparison.Ordinal) &&
                             !string.Equals(fencedRuPicked, "ENGLISH TEXT", StringComparison.Ordinal);
            if (!fencedRuOk) problems++;
            report.AppendLine($"Заметки {(fencedRuOk ? "ок" : "БЕДА")}: ограждение внутри перевода — русский текст читается (тело {fencedRuRaw.Length} знаков, выбран {fencedRuPicked.Length})");

            // Ограждение в каждом из трёх блоков: язык выбирается и после пересборки.
            var notesBodyFencedThree = "<!-- dsh-notes:en -->\nНАЧАЛО en\n```\n" + new string('E', 5000) + "\n```\nХВОСТ en\n<!-- /dsh-notes:en -->\n\n" +
                                       "<!-- dsh-notes:ru -->\nНАЧАЛО ru\n```\n" + new string('Р', 5000) + "\n```\nХВОСТ ru\n<!-- /dsh-notes:ru -->\n\n" +
                                       "<!-- dsh-notes:zh -->\nНАЧАЛО zh\n```\n" + new string('中', 5000) + "\n```\nХВОСТ zh\n<!-- /dsh-notes:zh -->";
            var fencedThreeRaw = UpdateService.RawNotes(notesBodyFencedThree);
            var fencedThreeOk = fencedThreeRaw.Length <= 20000 && fencedThreeRaw.Length > 0;
            foreach (var language in new[] { "en", "ru", "zh" })
            {
                var picked = UpdateService.PickNotes(fencedThreeRaw, language);
                if (picked.Length == 0 || picked.Length >= fencedThreeRaw.Length) fencedThreeOk = false;
            }
            if (!fencedThreeOk) problems++;
            report.AppendLine($"Заметки {(fencedThreeOk ? "ок" : "БЕДА")}: ограждения в трёх блоках — каждый язык читается (тело {fencedThreeRaw.Length} знаков)");

            // Блоков больше, чем предел делит на «запас»: запасного пути «обрезать сырое тело»
            // при найденных метках быть не должно, а русский блок обязан выжить целиком.
            var manyBody = new System.Text.StringBuilder("<!-- dsh-notes:ru -->\nНАСТОЯЩИЙ РУССКИЙ ТЕКСТ\n<!-- /dsh-notes:ru -->\n");
            for (var index = 0; index < 319; index++)
            {
                manyBody.Append("\n<!-- dsh-notes:x").Append(index).Append(" -->\nблок ").Append(index).Append(' ').Append('.', 20)
                        .Append("\n<!-- /dsh-notes:x").Append(index).Append(" -->\n");
            }
            var manyRaw = UpdateService.RawNotes(manyBody.ToString());
            var manyPicked = UpdateService.PickNotes(manyRaw, "ru");
            var manyOk = manyRaw.Length <= 20000 &&
                         string.Equals(manyPicked, "НАСТОЯЩИЙ РУССКИЙ ТЕКСТ", StringComparison.Ordinal);
            if (!manyOk) problems++;
            report.AppendLine($"Заметки {(manyOk ? "ок" : "БЕДА")}: 320 блоков — предел держится, русский блок выжил (тело {manyRaw.Length} знаков, выбран {manyPicked.Length})");

            // Длинные имена блоков: «обвязка» считается по настоящим маркерам, поэтому предел
            // остаётся пределом и здесь (в прежней правке имя в 5000 знаков его пробивало).
            var longName = new string('a', 5000);
            var longerName = new string('b', 5000);
            var notesBodyLongNames = "<!-- dsh-notes:" + longName + " -->\nтекст\n<!-- /dsh-notes:" + longName + " -->\n\n" +
                                     "<!-- dsh-notes:" + longerName + " -->\nтекст\n<!-- /dsh-notes:" + longerName + " -->";
            var longNamesRaw = UpdateService.RawNotes(notesBodyLongNames);
            var longNamesOk = longNamesRaw.Length <= 20000 && longNamesRaw.Length > 0 && !HasLonelySurrogate(longNamesRaw);
            if (!longNamesOk) problems++;
            report.AppendLine($"Заметки {(longNamesOk ? "ок" : "БЕДА")}: длинные имена блоков — предел держится (тело {longNamesRaw.Length} знаков)");

            // Имя длиннее самого предела: блок не влезает даже пустым — он отбрасывается, а не
            // заменяется обрезкой сырого тела.
            var hugeName = new string('c', 25000);
            var notesBodyHugeName = "<!-- dsh-notes:" + hugeName + " -->\nтекст\n<!-- /dsh-notes:" + hugeName + " -->";
            var hugeNameRaw = UpdateService.RawNotes(notesBodyHugeName);
            var hugeNameOk = hugeNameRaw.Length <= 20000 && !HasLonelySurrogate(hugeNameRaw);
            if (!hugeNameOk) problems++;
            report.AppendLine($"Заметки {(hugeNameOk ? "ок" : "БЕДА")}: имя блока длиннее предела — предел держится (тело {hugeNameRaw.Length} знаков)");

            // «<!--» в СЕРЕДИНЕ строки: оборванный комментарий не должен уехать в настройки —
            // правило одно для любого места строки, а не только для её начала.
            var notesBodyHalfComment = new string('x', 19990) + "<!--" + new string('y', 100);
            var halfCommentRaw = UpdateService.RawNotes(notesBodyHalfComment);
            var halfCommentOk = halfCommentRaw.Length <= 20000 && !HasOpenComment(halfCommentRaw);
            if (!halfCommentOk) problems++;
            report.AppendLine($"Заметки {(halfCommentOk ? "ок" : "БЕДА")}: оборванный «<!--» в середине строки не сохранён (тело {halfCommentRaw.Length} знаков)");

            // Обновления проверяются при КАЖДОМ запуске панели, поэтому шарик о новой версии
            // обязан показываться один раз на версию — иначе человек получал бы его на каждом старте.
            var sayNew = UpdateService.ShouldAnnounce("1.22.0", "", "1.21.0");
            var sayAgain = UpdateService.ShouldAnnounce("1.22.0", "1.22.0", "1.21.0");
            var sayOlder = UpdateService.ShouldAnnounce("1.20.0", "", "1.21.0");
            var sayEmpty = UpdateService.ShouldAnnounce("", "", "1.21.0");
            var sayNext = UpdateService.ShouldAnnounce("1.23.0", "1.22.0", "1.21.0");
            var saySame = UpdateService.ShouldAnnounce("1.21.0", "", "1.21.0");
            // Тег с «v» и хеш сборки — та же версия, а не новая; откат выпуска назад молчит.
            var sayTagged = UpdateService.ShouldAnnounce("v1.22.0", "1.22.0", "1.21.0");
            var sayHashed = UpdateService.ShouldAnnounce("1.22.0+91c7b69", "1.22.0", "1.21.0");
            var sayBack = UpdateService.ShouldAnnounce("1.21.1", "1.22.0", "1.21.0");
            var announceOk = sayNew && !sayAgain && !sayOlder && !sayEmpty && sayNext && !saySame
                             && !sayTagged && !sayHashed && !sayBack;
            if (!announceOk) problems++;
            report.AppendLine($"Обновления {(announceOk ? "ок" : "БЕДА")}: шарик один раз на версию " +
                              $"(новая {sayNew}, повтор {sayAgain}, старая {sayOlder}, пусто {sayEmpty}, " +
                              $"следующая {sayNext}, текущая {saySame}, тег {sayTagged}, хеш {sayHashed}, назад {sayBack})");

            // Шарик про автозапуск: изолированный прогон (подменены каталоги ИЛИ ветка реестра) не
            // показывает НИЧЕГО — наши проверки запускают собранную копию на машине владельца,
            // «ведёт на живую соседнюю копию» видно в меню трея, а вот неисправимую запись
            // человеку показываем.
            var notifyCheck = Autostart.ShouldNotify(Autostart.Where.Other, true);
            var notifyOther = Autostart.ShouldNotify(Autostart.Where.Other, false);
            var notifyMissing = Autostart.ShouldNotify(Autostart.Where.Missing, false);
            var notifyUnknown = Autostart.ShouldNotify(Autostart.Where.Unknown, false);
            var notifySelf = Autostart.ShouldNotify(Autostart.Where.Self, false);
            var notifyMissingInRun = Autostart.ShouldNotify(Autostart.Where.Missing, true);
            var notifyOk = !notifyCheck && !notifyOther && notifyMissing && notifyUnknown && !notifySelf
                           && !notifyMissingInRun;
            if (!notifyOk) problems++;
            report.AppendLine($"Автозапуск {(notifyOk ? "ок" : "БЕДА")}: шарик не мешает " +
                              $"(изолированный прогон {notifyCheck}, чужая копия {notifyOther}, " +
                              $"нет файла {notifyMissing}, реестр не читается {notifyUnknown}, " +
                              $"своя запись {notifySelf}, нет файла в прогоне {notifyMissingInRun})");

            // Автоматические сообщения: в изолированном прогоне молчит КАЖДЫЙ вид, а не один
            // шарик автозапуска, на котором мы уже обожглись (владелец получил от нашей проверки
            // сообщение про собственный автозапуск). Перебор идёт по самому перечислению видов:
            // новый вид автоматического сообщения нельзя добавить в стороне от предохранителя —
            // он попадёт в этот перебор сам. Через то же правило ходит единственная дверь
            // автоматических шариков (TrayHost.NotifyBalloon), поэтому кейс чувствителен к классу.
            var noticeKinds = Enum.GetValues<TrayHost.NoticeKind>();
            var noticeQuiet = noticeKinds.Count(kind => !TrayHost.ShouldNotify(kind, isolatedRun: true));
            var noticeLoud = noticeKinds.Count(kind => TrayHost.ShouldNotify(kind, isolatedRun: false));
            var noticesOk = noticeQuiet == noticeKinds.Length && noticeLoud == noticeKinds.Length;
            if (!noticesOk) problems++;
            report.AppendLine($"Уведомления {(noticesOk ? "ок" : "БЕДА")}: изолированный прогон молчит " +
                              $"(видов {noticeKinds.Length}: молчат {noticeQuiet}, говорят в своём прогоне {noticeLoud})");

            // И то же правило — про сам код, а не про намерение: читаем IL собранной панели и
            // смотрим, из каких методов вызывается ShowBalloonTip.
            //
            // Дверь определяется НАМЕРЕНИЕМ, а не именем: дверью считается любой метод, который
            // зовёт шарик и при этом спрашивает решение двери (TrayHost.DoorDecision). Так перенос
            // и переименование двери — улучшение, а не поломка, — кейс не валит, а шарик в обход
            // решения валит всегда. Кроме двери право звать ShowBalloonTip есть только у шариков
            // в ответ на действие человека (свёртывание окна в трей и только что применённые окна
            // пика): без человека они не появляются, поэтому это allow-list, который разрешено
            // сокращать (провести такой шарик через дверь — улучшение, и кейс молчит).
            var interactiveBalloons = new[] { "TrayHost.HideToTray", "TrayHost.OpenSettings" };
            var balloonSites = BalloonCallSites();
            var balloonDoors = balloonSites
                .Where(name => name == "TrayHost.NotifyBalloon" || CallsDoorDecision(name))
                .Distinct().ToList();
            var balloonStray = balloonSites
                .Where(name => !balloonDoors.Contains(name) && !interactiveBalloons.Contains(name))
                .Distinct().ToList();
            var balloonsOk = balloonDoors.Count > 0 && balloonStray.Count == 0;
            if (!balloonsOk) problems++;
            report.AppendLine($"Шарики {(balloonsOk ? "ок" : "БЕДА")}: дверь — " +
                              $"{string.Join(", ", balloonDoors)}" +
                              $"; ShowBalloonTip зовут {string.Join(", ", balloonSites.Distinct())}" +
                              (balloonStray.Count > 0 ? $"; мимо двери: {string.Join(", ", balloonStray)}" : "") +
                              (balloonDoors.Count == 0
                                  ? "; дверь не найдена: ни один шарик не идёт через TrayHost.DoorDecision"
                                  : ""));

            // Сервер при старте и окно по запросу второго запуска — две автоматические дороги к
            // владельцу, которые изолированный прогон обязан закрыть: прогон проверки не занимает
            // порт владельца и не кладёт окно на его рабочий стол. Явный путь к серверу
            // (--server-start) этими предикатами не закрыт — на нём стоит приёмка.
            var serverOwn = TrayHost.ShouldAutoStartServer(isolated: false);
            var serverIsolated = TrayHost.ShouldAutoStartServer(isolated: true);
            var signalOwn = TrayHost.ShouldShowPanelOnSignal(isolated: false);
            var signalIsolated = TrayHost.ShouldShowPanelOnSignal(isolated: true);
            var startupOk = serverOwn && !serverIsolated && signalOwn && !signalIsolated;
            if (!startupOk) problems++;
            report.AppendLine($"Автоматика старта {(startupOk ? "ок" : "БЕДА")}: " +
                              $"сервер — своему прогону {serverOwn}, изолированному {serverIsolated}; " +
                              $"окно по запросу второго запуска — своему {signalOwn}, изолированному {signalIsolated}");

            // Мастер первой настройки: четыре сочетания «прошёл / просили / изоляция» разворачиваются
            // в восемь проверок, и у каждого своё решение. Модальное окно мастера в изолированном
            // прогоне — это ровно то, что увидел владелец: панель поднимали без ключей из проверки
            // замены файлов, а таймер старта открывал мастер поверх его работы.
            var onboardingCases = new (bool Onboarded, bool Requested, bool Isolated, bool Expected)[]
            {
                (true, false, false, false),    // обычный прогон, мастер пройден — показывать нечего
                (true, false, true, false),     // изолированный прогон молчит
                (false, false, false, true),    // настоящий первый запуск — мастер показываем
                (false, false, true, false),    // ИЗОЛЯЦИЯ и мастер не пройден — не показываем
                (false, true, false, true),     // --onboard: мастер просили
                (false, true, true, true),      // --onboard в изоляции: проверка мастера видит окна
                (true, true, false, true),
                (true, true, true, true),
            };

            var onboardingWrong = new List<string>();
            foreach (var item in onboardingCases)
            {
                var actual = TrayHost.ShouldAutoShowOnboarding(item.Onboarded, item.Requested, item.Isolated);
                if (actual != item.Expected)
                {
                    onboardingWrong.Add($"(пройден {item.Onboarded}, просили {item.Requested}, " +
                                        $"изоляция {item.Isolated}) → {actual}, ждали {item.Expected}");
                }
            }

            var onboardingOk = onboardingWrong.Count == 0;
            if (!onboardingOk) problems++;
            report.AppendLine($"Мастер {(onboardingOk ? "ок" : "БЕДА")}: сочетаний {onboardingCases.Length}, " +
                              $"показываем {onboardingCases.Count(item => item.Expected)}" +
                              (onboardingOk ? "" : " — неверно: " + string.Join("; ", onboardingWrong)));

            // Видимое при старте: браузер и окно панели. В изолированном прогоне молчат оба —
            // это и есть граница «человек видит против проверка считает»: сервер поднимается
            // (его считает приёмка), а окна и браузера на рабочем столе владельца не появляется.
            var browserOwn = TrayHost.ShouldAutoOpenBrowser(isolated: false);
            var browserIsolated = TrayHost.ShouldAutoOpenBrowser(isolated: true);
            var windowOwn = TrayHost.ShouldShowWindowOnStart(onboarded: true, isolated: false);
            var windowFirstRun = TrayHost.ShouldShowWindowOnStart(onboarded: false, isolated: false);
            var windowIsolated = TrayHost.ShouldShowWindowOnStart(onboarded: true, isolated: true);
            var windowFirstRunIsolated = TrayHost.ShouldShowWindowOnStart(onboarded: false, isolated: true);
            var visibleOk = browserOwn && !browserIsolated && windowOwn && !windowFirstRun
                            && !windowIsolated && !windowFirstRunIsolated;
            if (!visibleOk) problems++;
            report.AppendLine($"Видимое при старте {(visibleOk ? "ок" : "БЕДА")}: " +
                              $"браузер — своему прогону {browserOwn}, изолированному {browserIsolated}; " +
                              $"окно — своему {windowOwn}, первому запуску {windowFirstRun}, " +
                              $"изолированному {windowIsolated}, первому запуску в изоляции {windowFirstRunIsolated}");

            // Признак изоляции проверяется НА САМОМ ДЕЛЕ, а не литералом: кейс сам выставляет и
            // снимает КАЖДУЮ переменную признака и смотрит, что Autostart.IsIsolatedRun это видит, а
            // ДВЕРЬ молчит. Дверь проверяется значением (TrayHost.DoorDecision), а не ссылкой на
            // предикат: дверь вида ShouldNotify(kind, IsIsolatedRun && NeverTrue()) ссылки имеет,
            // а показывает всё — ровно такая поломка проходила зелёной.
            //
            // Перечень держится ЛИТЕРАЛОМ — это документированный набор (красная линия №2 в
            // AGENTS.md и таблица подмен в docs\DEVELOPMENT.md) — и сверяется с признаком. Перебор
            // по самому признаку этого не поймал бы: сузят признак (выбросят DSH_HOME — ровно то,
            // что чинила эта партия) — сузится и перебор, и кейс останется зелёным. Появилась новая
            // подмена — сверка покраснеет и заставит дописать её сюда и в документацию.
            var documentedIsolation = new[]
            {
                "DSH_PANEL_DATA", "DSH_PANEL_STATE", "DSH_PANEL_SSH_DIR", "DSH_TRAY_BACKUP",
                "DSH_HOME", "DSH_PANEL_LEGACY_DATA", "DSH_PANEL_INSTANCE", "DSH_PANEL_RUN_KEY",
                "DSH_PANEL_NO_MIGRATE",
            };
            var isolateNames = documentedIsolation;
            // Подмены «на один прогон», которые изоляцией НЕ считаются: ими пользуется человек
            // (свой Node, свой язык, своя страница цен), их называет README, и по ним глушить окна
            // и шарики нельзя. Проверяем и это — иначе предохранитель однажды расширят «на всякий
            // случай» и панель замолчит у человека с переносимым Node.
            var notIsolation = new[]
            {
                "DSH_PANEL_LANG", "DSH_TRAY_PRICING", "DSH_PANEL_API", "DSH_PANEL_REPO",
                "DSH_TRAY_NODE", "DSH_TRAY_BIN",
            };
            var isolateAll = isolateNames.Concat(notIsolation).ToArray();
            var isolateSaved = isolateAll
                .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
                .ToList();
            var isolateNotes = new List<string>();
            var isolateOk = true;

            // Признак обязан считать ровно документированный набор — ни больше, ни меньше.
            var markerExtra = Autostart.IsolationOverrideNames.Except(isolateNames).ToList();
            var markerMissing = isolateNames.Except(Autostart.IsolationOverrideNames).ToList();
            var markerMatchesDocs = markerExtra.Count == 0 && markerMissing.Count == 0;
            isolateOk &= markerMatchesDocs;
            isolateNotes.Add(markerMatchesDocs
                ? "набор признака совпадает с документированным"
                : "набор признака разошёлся с документированным: лишние " +
                  string.Join(", ", markerExtra) + "; пропали " + string.Join(", ", markerMissing));

            void ClearIsolation()
            {
                foreach (var name in isolateAll) Environment.SetEnvironmentVariable(name, null);
            }

            try
            {
                foreach (var name in isolateNames)
                {
                    ClearIsolation();
                    Environment.SetEnvironmentVariable(name, "1");

                    var seen = Autostart.IsIsolatedRun;
                    // Дверь — тем же признаком, посчитанным из окружения: кейс ловит и признак,
                    // переставший видеть переменную, и дверь, переставшую эту переменную слушать.
                    // Про молчание двери пишем только когда оно нарушено: иначе строка отчёта
                    // распухает так, что причину «БЕДА» в ней не найти.
                    var quiet = noticeKinds.All(kind => !TrayHost.DoorDecision(kind));
                    // Заодно признак «прогон проверки»: по нему панель вообще не пишет в настоящий
                    // HKCU\...\Run. Единственное исключение — уведённая ветка реестра: с ней починку
                    // упражняет check-autostart, и там запись править как раз можно.
                    var checkRun = Autostart.IsCheckRun;
                    var checkRunOk = checkRun == (name != "DSH_PANEL_RUN_KEY");
                    isolateOk &= seen && quiet && checkRunOk;
                    isolateNotes.Add($"{name}: признак {seen}" + (quiet ? "" : ", дверь ГОВОРИТ")
                                     + (checkRunOk ? "" : $", прогон проверки {checkRun}"));
                }

                ClearIsolation();
                var none = Autostart.IsIsolatedRun;
                var loud = noticeKinds.All(kind => TrayHost.DoorDecision(kind));

                // Подмены на один прогон: признак их не видит, и панель у такого человека говорит.
                var declared = new List<string>();
                foreach (var name in notIsolation)
                {
                    ClearIsolation();
                    Environment.SetEnvironmentVariable(name, "1");

                    var counted = Autostart.IsIsolatedRun;
                    var speaks = noticeKinds.All(kind => TrayHost.DoorDecision(kind));
                    isolateOk &= !counted && speaks;
                    declared.Add($"{name}: {counted}" + (speaks ? "" : ", дверь МОЛЧИТ"));
                }

                // Значение из пробелов признаком не считается: пустую строку в окружении получить
                // легко, и она не должна глушить панель у человека.
                ClearIsolation();
                Environment.SetEnvironmentVariable("DSH_PANEL_DATA", "   ");
                var spaces = Autostart.IsIsolatedRun;

                isolateOk &= !none && loud && !spaces;
                isolateNotes.Add($"нет переменных: {none}" + (loud ? "" : ", дверь МОЛЧИТ"));
                isolateNotes.Add($"пробелы: {spaces}");
                isolateNotes.Add("на один прогон не считаются: " + string.Join(", ", declared));
            }
            finally
            {
                // Возвращаем окружение как было: от него зависит смысл остальных кейсов, а сама
                // самопроверка идёт изолированным прогоном (переменные выставлены снаружи).
                foreach (var item in isolateSaved) Environment.SetEnvironmentVariable(item.Name, item.Value);
            }

            if (!isolateOk) problems++;
            report.AppendLine($"Изоляция {(isolateOk ? "ок" : "БЕДА")}: признак виден по каждой из " +
                              $"{isolateNames.Length} переменных, дверь молчит по значению " +
                              $"({string.Join(", ", isolateNotes)})");

            // Предикат бесполезен, если решение его не спрашивает. Кейс читает IL самих решающих
            // методов и требует, чтобы каждый звал СВОЙ предикат и общий признак изоляции. Так
            // ловится то, что до сих пор проходило зелёным на одних литералах: дверь шариков,
            // переставшая спрашивать изоляцию (ShouldNotify(kind, false)), снятый гейт мастера,
            // браузер без проверки, а теперь и самозапуск сервера.
            //
            // Дверь изоляцию сама не читает: её читает решение двери (OwnIsolation = false у двери,
            // true у DoorDecision) — правило одно, и живёт оно в одном месте.
            var gateRules = new (string Owner, string Predicate, bool OwnIsolation)[]
            {
                ("NotifyBalloon", "TrayHost.DoorDecision", false),
                ("DoorDecision", "TrayHost.ShouldNotify", true),
                ("MaybeShowOnboarding", "TrayHost.ShouldAutoShowOnboarding", true),
                ("ShowPanelOnStart", "TrayHost.ShouldShowWindowOnStart", true),
                ("OnShowEventRequested", "TrayHost.ShouldShowPanelOnSignal", true),
                ("StartInitialServer", "TrayHost.ShouldAutoOpenBrowser", true),
                ("StartInitialServer", "TrayHost.ShouldAutoStartServer", true),
            };

            var gateProblems = new List<string>();
            foreach (var rule in gateRules)
            {
                var owner = PanelMethod(rule.Owner);
                if (owner == null)
                {
                    gateProblems.Add(rule.Owner + ": метода нет");
                    continue;
                }

                var references = MethodReferences(owner);
                if (!references.Contains(rule.Predicate))
                {
                    gateProblems.Add(rule.Owner + " не спрашивает " + rule.Predicate);
                }

                if (rule.OwnIsolation && !references.Contains("Autostart.IsIsolatedRun"))
                {
                    gateProblems.Add(rule.Owner + " не спрашивает изоляцию");
                }
            }

            // Мастер обязан быть достижим: «гейт на месте» ничего не стоит, если его никто не зовёт.
            var onboardingReachable = typeof(TrayHost).Assembly.GetTypes()
                .SelectMany(DeclaredMethods)
                .Any(method => method.Name != "MaybeShowOnboarding"
                               && MethodReferences(method).Contains("TrayHost.MaybeShowOnboarding"));
            if (!onboardingReachable) gateProblems.Add("MaybeShowOnboarding: никто не зовёт — мастер недостижим");

            // Дверь обязана быть достижима ровно так же: решение, которое никто не зовёт, молчит
            // «само по себе», а не потому, что изоляция.
            var doorReachable = typeof(TrayHost).Assembly.GetTypes()
                .SelectMany(DeclaredMethods)
                .Any(method => method.Name != "DoorDecision"
                               && MethodReferences(method).Contains("TrayHost.DoorDecision"));
            if (!doorReachable) gateProblems.Add("DoorDecision: никто не зовёт — дверь недостижима");

            var gatesOk = gateProblems.Count == 0;
            if (!gatesOk) problems++;
            report.AppendLine($"Гейты {(gatesOk ? "ок" : "БЕДА")}: решение спрашивают " +
                              $"({string.Join(", ", gateRules.Select(rule => rule.Owner).Distinct())})" +
                              (gatesOk ? "" : " — " + string.Join("; ", gateProblems)));

            var icons = new[] { ServerVisual.Running, ServerVisual.Stopped, ServerVisual.BusyOther };
            foreach (var visual in icons)
            {
                using var icon = TrayIconFactory.Create(visual, true, 32);
                using var offPeak = TrayIconFactory.Create(visual, false, 32);
                report.AppendLine($"Иконка {visual}: {icon.Size.Width}px (пик) и {offPeak.Size.Width}px (вне пика) — ок");
            }

            // Оформление (1.22.0): палитра обязана быть полной, а контраст — достаточным
            // в обеих темах. Порог 4,5 (WCAG AA) — для текста, 1,2 — для рамки: она должна
            // быть видна, а не читаться. Перебираем обе палитры через Theme.PaletteFor, а не
            // текущую тему: тёмную включает настройка, и без перебора она осталась бы
            // непроверенной. Пары цветов взяты там, где они действительно встречаются: текст
            // и подписи на окне, на карточке и на полосе, текст главной кнопки на акценте,
            // цвета состояния на карточке, рамка на карточке.
            string Number(double value) =>
                value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

            foreach (var dark in new[] { false, true })
            {
                var palette = Theme.PaletteFor(dark);
                var theme = dark ? "тёмная" : "светлая";

                // Роль без цвета — это дырка в палитре: окно, которое её попросит, нарисует
                // чёрное или прозрачное пятно вместо цвета темы.
                var undefined = typeof(Theme.Palette).GetProperties()
                    .Where(role => role.GetValue(palette) is Color color && (color.IsEmpty || color.A != 255))
                    .Select(role => role.Name)
                    .ToList();

                // Надпись главной кнопки: Theme.Button берёт в тёмной теме цвет окна вместо
                // белого — сверяем ровно тот цвет, который она и нарисует.
                var onAccent = dark ? palette.Window : Color.White;

                var checks = new (string What, double Ratio, double Need)[]
                {
                    ("текст на окне", Theme.Contrast(palette.Text, palette.Window), 4.5),
                    ("текст на карточке", Theme.Contrast(palette.Text, palette.Surface), 4.5),
                    ("текст на полосе", Theme.Contrast(palette.Text, palette.SurfaceAlt), 4.5),
                    ("подпись на карточке", Theme.Contrast(palette.Muted, palette.Surface), 4.5),
                    ("текст главной кнопки на акценте", Theme.Contrast(onAccent, palette.Accent), 4.5),
                    ("«работает» на карточке", Theme.Contrast(palette.Success, palette.Surface), 4.5),
                    ("предупреждение на карточке", Theme.Contrast(palette.Warning, palette.Surface), 4.5),
                    ("ошибка на карточке", Theme.Contrast(palette.Danger, palette.Surface), 4.5),
                    ("рамка на карточке", Theme.Contrast(palette.Border, palette.Surface), 1.2),
                };

                var failed = checks.Where(check => check.Ratio < check.Need)
                    .Select(check => $"«{check.What}» {Number(check.Ratio)} (нужно {Number(check.Need)})")
                    .ToList();

                // «Хуже всего» — по запасу до порога (отношение к порогу), а не по самому
                // маленькому числу: у рамки порог свой, и 1,24 несравнимо с 4,5.
                var worst = checks.OrderBy(check => check.Ratio / check.Need).First();
                var contrastOk = undefined.Count == 0 && failed.Count == 0;
                if (!contrastOk) problems++;

                report.AppendLine($"Оформление {(contrastOk ? "ок" : "БЕДА")}: палитра {theme} — " +
                                  $"ролей {typeof(Theme.Palette).GetProperties().Length}, проверок {checks.Length}, " +
                                  $"хуже всего «{worst.What}» {Number(worst.Ratio)} (порог {Number(worst.Need)})" +
                                  (undefined.Count > 0 ? ", роли без цвета: " + string.Join(", ", undefined) : "") +
                                  (failed.Count > 0 ? ", мало контраста: " + string.Join("; ", failed) : ""));
            }

            // Тема оформления (1.22.0): строка настройки и состояние окна обязаны сходиться в обе
            // стороны. Пустое, пробельное и неизвестное значение — это «как в Windows»: так
            // выглядят settings.json прежних версий, где поля theme нет вовсе, и человек не должен
            // получить из-за этого тёмное окно. Сверяем и обратный перевод (ModeTo): именно им
            // панель пишет настройку, и разошедшаяся пара «light» ↔ dark пережила бы перезапуск.
            var themeCases = new (string Value, Theme.Mode Mode, string Text)[]
            {
                ("auto", Theme.Mode.Auto, "auto"),
                ("light", Theme.Mode.Light, "light"),
                ("dark", Theme.Mode.Dark, "dark"),
                ("", Theme.Mode.Auto, "auto"),
                ("   ", Theme.Mode.Auto, "auto"),
                ("DARK", Theme.Mode.Dark, "dark"),
                ("тёмная", Theme.Mode.Auto, "auto"),
            };

            var themeWrong = new List<string>();
            foreach (var item in themeCases)
            {
                var mode = Theme.ModeFrom(item.Value);
                var text = Theme.ModeTo(mode);
                if (mode != item.Mode || text != item.Text)
                {
                    themeWrong.Add($"«{item.Value}» → {mode}/{text}, ждали {item.Mode}/{item.Text}");
                }
            }

            // И то же самое до состояния окна: Use обязан переключить палитру, а не только
            // запомнить строку. Светлую и тёмную проверяем по очереди, а в конце возвращаем
            // состояние каким оно было: самопроверка строит окна дальше (TrayHost ниже), и
            // оставленная тёмная тема поменяла бы их вид — то есть проверила бы не то.
            var wasDark = Theme.IsDark;
            Theme.Use(Theme.Mode.Light);
            var themeLightOk = !Theme.IsDark;

            // Значок трея рисуется своей тёмной плиткой (TrayIconFactory) и от палитры окна не
            // зависит: сверяем кадры по пикселям в обеих темах. Если значок когда-нибудь начнёт
            // брать цвет из темы, кадры разойдутся — а человек получит выцветший значок.
            using var iconLight = TrayIconFactory.Render(ServerVisual.Running, false, 32);

            Theme.Use(Theme.Mode.Dark);
            var themeDarkOk = Theme.IsDark;
            using var iconDark = TrayIconFactory.Render(ServerVisual.Running, false, 32);
            var themeIconOk = SameBitmap(iconLight, iconDark);

            Theme.Use(wasDark ? Theme.Mode.Dark : Theme.Mode.Light);
            var themeRestoredOk = Theme.IsDark == wasDark;

            var themeOk = themeWrong.Count == 0 && themeLightOk && themeDarkOk
                          && themeRestoredOk && themeIconOk;
            if (!themeOk) problems++;
            report.AppendLine($"Тема {(themeOk ? "ок" : "БЕДА")}: режим из настройки и состояние окна " +
                              $"(светлая {themeLightOk}, тёмная {themeDarkOk}, состояние возвращено {themeRestoredOk}, " +
                              $"значок трея один в обеих темах {themeIconOk}; " +
                              string.Join(", ", themeCases.Select(item =>
                                  $"«{item.Value}»→{Theme.ModeTo(Theme.ModeFrom(item.Value))}")) + ")" +
                              (themeWrong.Count > 0 ? " — неверно: " + string.Join("; ", themeWrong) : ""));

            // Тема обязана не только переключаться, но и доходить до ОТКРЫТЫХ окон: Retext
            // пересобирает контролы, и без Theme.Apply внутри него вид после смены языка вернулся
            // бы к системным цветам — это отдельный пункт приёмки 1.22.0. Смотрим IL, а не
            // намерение: так ловится и снятый вызов, и забытая пересборка окна панели при смене
            // темы. Пары «кто — что обязан звать»: оба Retext применяют тему, а обе двери смены
            // (язык и тема) решают палитру через Theme.Use и пересобирают окно панели.
            var themeSites = new (string Method, string Need)[]
            {
                ("MainForm.Retext", "Theme.Apply"),
                ("SettingsForm.Retext", "Theme.Apply"),
                ("TrayHost.ApplyLanguage", "Theme.Use"),
                ("TrayHost.ApplyLanguage", "MainForm.Retext"),
                ("TrayHost.ApplyTheme", "Theme.Use"),
                ("TrayHost.ApplyTheme", "MainForm.Retext"),
            };

            var themeMissing = new List<string>();
            foreach (var site in themeSites)
            {
                var method = typeof(TrayHost).Assembly.GetTypes()
                    .SelectMany(type => DeclaredMethods(type).Select(item => (type, method: item)))
                    .FirstOrDefault(item => item.type.Name + "." + item.method.Name == site.Method).method;

                if (method == null) themeMissing.Add(site.Method + ": метода нет");
                else if (!MethodReferences(method).Contains(site.Need))
                    themeMissing.Add(site.Method + " не зовёт " + site.Need);
            }

            var themeWiredOk = themeMissing.Count == 0;
            if (!themeWiredOk) problems++;
            report.AppendLine($"Тема в окнах {(themeWiredOk ? "ок" : "БЕДА")}: " +
                              $"проверок {themeSites.Length} — окно пересобирается с темой, смена доходит до панели" +
                              (themeWiredOk ? "" : " — " + string.Join("; ", themeMissing)));

            // Смена языка и темы пересобирает окно настроек целиком (Retext). Окно обязано
            // остаться ТЕМ ЖЕ окном: подписи внутри карточек живут не в странице вкладки, а в
            // самой карточке, и без её очистки каждая смена оставляла бы в карточке ещё один
            // слой тех же подписей — глазом это не видно (слои совпадают), но вид и вес окна
            // портит, а смена темы теперь идёт через тот же путь. Считаем контролы дерева до и
            // после двух лишних пересборок: число обязано совпасть. Окно не показываем — только
            // строим: на рабочем столе владельца не должно появиться ничего.
            var retextCounts = "";
            try
            {
                using var settingsWindow = new SettingsForm(settings, paths, new PricingUpdateService(paths), () => "");
                settingsWindow.Retext();
                var retextOnce = ControlCount(settingsWindow);
                settingsWindow.Retext();
                settingsWindow.Retext();
                var retextMany = ControlCount(settingsWindow);

                var retextOk = retextOnce > 0 && retextOnce == retextMany;
                if (!retextOk) problems++;
                retextCounts = $"{retextOnce} контролов после пересборки, {retextMany} после трёх";
                report.AppendLine($"Пересборка окна {(retextOk ? "ок" : "БЕДА")}: Retext не накапливает " +
                                  $"контролы ({retextCounts})");
            }
            catch (Exception error)
            {
                problems++;
                report.AppendLine("Пересборка окна БЕДА: окно настроек не построилось — " + error.Message);
            }

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var host = new TrayHost(startHidden: true, showEvent: showEvent, port: options.Port, autoStart: false);
            report.AppendLine(host.SelfTestReport());
            report.AppendLine(problems == 0 ? "SELFTEST OK" : $"SELFTEST: ПРОБЛЕМ {problems}");

            WriteReport(report.ToString(), options.OutPath);
            return problems == 0 ? 0 : 1;
        }

        WriteReport(report.ToString(), options.OutPath);
        return 0;
    }

    /// <summary>
    /// Сколько контролов в дереве окна. Нужно самопроверке: пересборка окна (<c>Retext</c>)
    /// не должна накапливать контролы — лишний слой подписей не виден глазом, но вид портит.
    /// </summary>
    private static int ControlCount(Control root)
    {
        var count = 0;
        foreach (Control control in root.Controls)
        {
            count += 1 + ControlCount(control);
        }

        return count;
    }

    /// <summary>
    /// Одинаковы ли два кадра по пикселям. Нужно самопроверке: значок трея рисуется своей
    /// тёмной плиткой и обязан быть одним и тем же в светлой и тёмной теме — если он начнёт
    /// брать цвет из палитры окна, кадры разойдутся.
    /// </summary>
    private static bool SameBitmap(Bitmap first, Bitmap second)
    {
        if (first.Width != second.Width || first.Height != second.Height) return false;

        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Есть ли в тексте комментарий «&lt;!--» без закрывающего «--&gt;»: такой обрывок не
    /// должен попадать в settings.json — заметки обязаны оставаться целым текстом.
    /// </summary>
    private static bool HasOpenComment(string text)
    {
        var open = text.LastIndexOf("<!--", StringComparison.Ordinal);
        if (open < 0) return false;

        return text.LastIndexOf("-->", StringComparison.Ordinal) < open;
    }

    /// <summary>
    /// Есть ли в строке одинокий суррогат UTF-16: старший без младшего следом или младший без
    /// старшего перед ним. Такую строку System.Text.Json запишет в settings.json как «\uFFFD»,
    /// то есть значение не переживёт запись и чтение.
    /// </summary>
    private static bool HasLonelySurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) return true;
                index++;
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Из каких методов панели вызывается <c>NotifyIcon.ShowBalloonTip</c>: читаем IL собранного
    /// кода, потому что «все автоматические шарики идут через одну дверь» иначе остаётся
    /// утверждением на слово. Смотрим всю сборку целиком (включая вложенные типы: асинхронные
    /// методы компилятор переносит в автомат состояния, и вызов шарика из <c>async</c>-метода
    /// лежал бы именно там), а не один TrayHost — иначе шарик, добавленный в другом файле,
    /// прошёл бы мимо проверки.
    ///
    /// Ловится и прямой вызов, и ссылка на метод для делегата (<c>ldftn</c>/<c>ldvirtftn</c>):
    /// проверяющий показал, что делегат обходил прежний обход по одним 0x28/0x6F. Отражение по
    /// имени (<c>GetMethod("ShowBalloonTip").Invoke(...)</c>) не ловится и поймано быть не может —
    /// в этом случае имя метода обычная строка, а не ссылка; что кейс гарантирует, а что нет,
    /// сказано в docs\DEVELOPMENT.md.
    /// </summary>
    /// <summary>
    /// Спрашивает ли метод панели с именем «Тип.Метод» решение двери (<c>TrayHost.DoorDecision</c>).
    /// Так дверь узнаётся по намерению, а не по имени: правило «все автоматические шарики идут
    /// через дверь» остаётся верным и после переноса или переименования двери.
    /// </summary>
    private static bool CallsDoorDecision(string name)
    {
        foreach (var type in typeof(TrayHost).Assembly.GetTypes())
        {
            foreach (var method in DeclaredMethods(type))
            {
                if (type.Name + "." + method.Name != name) continue;
                return MethodReferences(method).Contains("TrayHost.DoorDecision");
            }
        }

        return false;
    }

    private static List<string> BalloonCallSites()
    {
        const string balloon = "NotifyIcon.ShowBalloonTip";
        var found = new List<string>();

        foreach (var type in typeof(TrayHost).Assembly.GetTypes())
        {
            foreach (var method in DeclaredMethods(type))
            {
                if (MethodReferences(method).Contains(balloon))
                {
                    found.Add(type.Name + "." + method.Name);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Все методы типа, объявленные в нём самом: унаследованные тела к делу не относятся.
    /// </summary>
    private static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                        | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    /// <summary>
    /// На какие методы ссылается тело метода: <c>call</c>/<c>callvirt</c> (вызов) и
    /// <c>ldftn</c>/<c>ldvirtftn</c> (адрес метода — так компилятор делает делегат). Имя
    /// возвращается как «Тип.Метод», у свойств снимается «get_»/«set_»: в IL обращение к свойству
    /// выглядит вызовом метода, а в правилах удобнее видеть имя свойства.
    ///
    /// Неудача разбора токена — не беда: байт <c>0x28</c> может попасться внутри чужого операнда,
    /// такой токен просто пропускается.
    /// </summary>
    private static List<string> MethodReferences(MethodBase method)
    {
        var found = new List<string>();

        byte[] il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return found;   // тела нет (сгенерированный метод) — и ссылок в нём нет
        }

        if (il == null) return found;

        for (var index = 0; index + 4 < il.Length; index++)
        {
            int operand;
            if (il[index] == 0x28 || il[index] == 0x6F)
            {
                // 0x28 — call, 0x6F — callvirt; сразу за опкодом идёт токен метода.
                operand = index + 1;
            }
            else if (il[index] == 0xFE && index + 5 < il.Length
                     && (il[index + 1] == 0x06 || il[index + 1] == 0x07))
            {
                // 0xFE 0x06 — ldftn, 0xFE 0x07 — ldvirtftn; токен идёт через байт.
                operand = index + 2;
            }
            else
            {
                continue;
            }

            try
            {
                var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, operand));
                if (target?.DeclaringType != null) found.Add(MethodLabel(target));
            }
            catch
            {
                // Чужой байт — пропускаем.
            }
        }

        return found;
    }

    /// <summary>Имя метода для правил: «Тип.Метод», у свойств — без «get_»/«set_».</summary>
    private static string MethodLabel(MethodBase method)
    {
        var name = method.Name;
        if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal))
        {
            name = name[4..];
        }

        return (method.DeclaringType?.Name ?? "?") + "." + name;
    }

    /// <summary>Метод панели по имени — для структурных кейсов самопроверки.</summary>
    private static MethodBase PanelMethod(string name) =>
        typeof(TrayHost).GetMethod(name, BindingFlags.Instance | BindingFlags.Static
                                         | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>Код ответа на запрос HEAD: «200», «404» или текст ошибки. Для --node-check.</summary>
    private static string Probe(string url)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DshTray/" + AppVersion.Short);
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            return ((int)response.StatusCode).ToString();
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }
    private static void WriteIconPreview(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        const int cell = 32;
        const int scale = 4;
        const int pad = 24;
        var width = pad * 2 + cell * scale * 3;
        var height = pad * 2 + (cell * scale + 28) * 2;

        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var visuals = new[] { ServerVisual.Running, ServerVisual.Stopped, ServerVisual.BusyOther };
        using var caption = new Font("Segoe UI", 10F);
        using var brush = new SolidBrush(Color.FromArgb(30, 30, 30));

        for (var row = 0; row < 2; row++)
        {
            var peak = row == 0;
            for (var column = 0; column < visuals.Length; column++)
            {
                var x = pad + column * cell * scale;
                var y = pad + row * (cell * scale + 28);

                using var source = TrayIconFactory.Render(visuals[column], peak, cell);
                graphics.DrawImage(source, new Rectangle(x, y, cell * scale, cell * scale));

                var label = $"{visuals[column]} · {(peak ? "пик" : "вне пика")}";
                graphics.DrawString(label, caption, brush, x, y + cell * scale + 4);
            }
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void WriteReport(string text, string outPath)
    {
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
            }
            catch
            {
                // Отчёт не сохранился — попробуем хотя бы напечатать.
            }
        }

        try
        {
            AttachConsole(-1);
            // Консоль, к которой мы подключились, живёт в своей кодовой странице
            // (обычно 866 или 1251), и запись в неё UTF-8 давала мусор вместо кириллицы.
            // Переключаем её на UTF-8 только на время вывода и возвращаем прежнюю
            // сразу после записи, иначе поедет вывод родительской оболочки.
            var previousCodePage = GetConsoleOutputCP();
            try
            {
                SetConsoleOutputCP(65001);
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.Out.Write(text);
                Console.Out.Flush();
            }
            finally
            {
                if (previousCodePage != 0) SetConsoleOutputCP(previousCodePage);
            }
        }
        catch
        {
            // Консоли нет — отчёт остался в файле.
        }
    }

    /// <summary>
    /// Состояние автозапуска словами. Человеку и в жалобе важно не «да/нет», а КУДА ведёт
    /// запись: «включён» при записи на чужую копию — это ровно та путаница, из-за которой
    /// после перезагрузки поднималась старая панель.
    /// </summary>
    private static string DescribeAutostart(Autostart.State state) => state.Where switch
    {
        Autostart.Where.None => Loc.T("cli.autostartOff"),
        Autostart.Where.Self => Loc.T("cli.autostartSelf"),
        Autostart.Where.Missing => Loc.T("cli.autostartMissing",
            state.Path.Length > 0 ? state.Path : Loc.T("common.dash")),
        Autostart.Where.Older => Loc.T("cli.autostartOlder", state.Path),
        Autostart.Where.Unknown => Loc.T("cli.autostartUnknown"),
        _ => Loc.T("cli.autostartOther", state.Path),
    };

    /// <summary>
    /// Починка автозапуска по команде: тот же код, что панель выполняет при старте
    /// (<see cref="Autostart.Repair"/>). Печатает состояние до и после и совершённое действие,
    /// чтобы проверка могла утверждать результат, а человек — увидеть причину.
    ///
    /// Кроме русского текста печатается машинная строка <c>action=…</c>: проверка сверяет
    /// действие по ней, а не по русской подписи, — иначе проверка ломалась бы на английской
    /// Windows или от любой правки формулировки.
    /// </summary>
    private static string AutostartFixReport()
    {
        var report = new StringBuilder();
        var fix = Autostart.Repair();

        report.AppendLine("До:       " + Loc.T("cli.autostart", DescribeAutostart(fix.Before)));
        report.AppendLine("После:    " + Loc.T("cli.autostart", DescribeAutostart(fix.After)));
        report.AppendLine("Действие: " + (fix.Changed ? fix.Action : "не требовалось"));
        report.AppendLine("action=" + (fix.Changed ? fix.Action : "none"));
        report.AppendLine("dedup=" + (fix.HadDuplicate ? "true" : "false"));
        return report.ToString();
    }

    /// <summary>
    /// Проверка словарей: для каждого языка печатает одни и те же подписи. Снимки окон для
    /// этого не нужны — видно, что язык применился, что переводы не совпадают и каких
    /// ключей не хватает.
    /// </summary>
    private static string LanguageCheckReport()
    {
        var keys = new[]
        {
            "app.title", "main.start", "main.restart", "main.stop", "main.open",
            "main.groupState", "main.groupBalance", "main.refreshBalance", "main.platform",
            "state.runningTitle", "state.stoppedTitle", "state.portBusyTitle",
            "tray.openPanel", "tray.backups", "tray.exit",
            "tray.backupFailedTitle", "tray.pricingChangedTitle", "tray.peaksApplied",
            "busy.startServer", "busy.autostart", "err.autostart",
            "peak.state.offPeak", "peak.days.workweek", "peak.next.line", "peak.err.badTime",
            "prices.title", "prices.copy", "prices.column.item",
        };

        // Словари сверяются целиком, а не по горстке строк: раньше пропажа ключа в английском
        // или китайском проходила мимо этой проверки, и человек видел в окне сам ключ.
        var gaps = Loc.CompareLanguages(out var totalKeys);

        var report = new StringBuilder();
        report.AppendLine($"ключей во всех языках: {totalKeys}");
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            Loc.Init(language);
            report.AppendLine($"[{language}] строк в словаре: {Loc.KeyCount}, шрифт: {Loc.UiFont(9F).FontFamily.Name}");
            report.AppendLine("  " + string.Join(" | ", keys.Select(key => Loc.T(key))));

            var missing = gaps[language];
            report.AppendLine(missing.Count == 0
                ? "  пропущенных ключей нет"
                : "  НЕТ КЛЮЧЕЙ: " + string.Join(", ", missing));

            // Ключи, которых нет ни в языке, ни в английском: их видно в окне как есть.
            var used = Loc.MissingKeys.ToArray();
            if (used.Length > 0) report.AppendLine("  нет ни в языке, ни в английском: " + string.Join(", ", used));
        }

        return report.ToString();
    }

    /// <summary>
    /// Что панель нашла в системе: node, пакет dsh, хранилище с ключом, занят ли порт.
    /// Тем же пользуется мастер настройки, а человеку этот отчёт удобно переслать,
    /// когда что-то не работает.
    /// </summary>
    /// <summary>
    /// Есть ли среда .NET Desktop 8 и какая именно версия. Смотрим ПАПКИ установки, а не реестр:
    /// на машине с установленной средой (8.0.31 в Program Files\dotnet\shared) нужной ветки
    /// реестра может не быть вовсе — проверка по реестру врала, и установщик бесконечно требовал
    /// поставить среду заново (это и увидел владелец на приёмке).
    /// </summary>
    private static string DesktopRuntimeVersion()
    {
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;

                var eight = Directory.GetDirectories(root)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name) && name.StartsWith("8.", StringComparison.Ordinal))
                    .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(eight)) return eight;
            }
        }
        catch
        {
            // Не смогли посмотреть папки — считаем, что не нашли.
        }

        return "";
    }

    private static string EnvironmentReport(AppSettings settings, AppPaths paths)
    {
        var report = new StringBuilder();
        report.AppendLine("язык: " + Loc.Language);

        // Среда .NET: панель собрана под .NET Desktop 8, и без неё не запустится вовсе.
        var desktop = DesktopRuntimeVersion();
        report.AppendLine(desktop.Length > 0
            ? Loc.T("env.dotnetFound", desktop)
            : Loc.T("env.dotnetMissing"));

        try
        {
            report.AppendLine(Loc.T("onboard.env.nodeFound", AppPaths.Display(NodeLocator.ResolveNode())));
        }
        catch
        {
            report.AppendLine(Loc.T("onboard.env.nodeMissing"));
        }

        try
        {
            report.AppendLine(Loc.T("onboard.env.dshFound", AppPaths.Display(NodeLocator.ResolveDshBin())));
        }
        catch
        {
            report.AppendLine(Loc.T("onboard.env.dshMissing"));
        }

        var credentials = AppPaths.Display(AppPaths.CredentialsPath);
        report.AppendLine(File.Exists(credentials)
            ? Loc.T("env.credentialsFound", credentials)
            : Loc.T("balance.storageMissing", credentials));

        var port = settings.ServerPort;
        var (occupantPid, occupantName, ours) = ServerController.Listener(port, paths);
        if (occupantPid == 0)
        {
            report.AppendLine(Loc.T("onboard.port.free") + " (" + port + ")");
        }
        else
        {
            // Порт, занятый своей же панелью, — не помеха, и пугать им человека незачем.
            report.AppendLine(ours
                ? Loc.T("onboard.port.ours", occupantPid)
                : Loc.T("onboard.port.busy", occupantName, occupantPid));
        }

        report.AppendLine(Loc.T("onboard.dir.title") + " "
                          + (string.IsNullOrWhiteSpace(settings.ServerWorkingDir) ? AppContext.BaseDirectory : settings.ServerWorkingDir));

        return report.ToString();
    }

    private static string ProcessName(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string HelpText()
    {
        // Синтаксис ключей — латиницей, описание — из словарей: так столбик не разъезжается
        // при переводе на английский и китайский.
        var lines = new List<string>
        {
            Loc.T("cli.help.title"),
            "",
            "  DshTray.exe — " + Loc.T("cli.help.open"),
            "  DshTray.exe --tray (--hidden) — " + Loc.T("cli.help.tray"),
            "  DshTray.exe --onboard — " + Loc.T("cli.help.onboard"),
            "  DshTray.exe --status [--out <file>] — " + Loc.T("cli.help.status"),
            "  DshTray.exe --env-check [--out <file>] — " + Loc.T("cli.help.envCheck"),
            "  DshTray.exe --autostart-fix [--out <file>] — " + Loc.T("cli.help.autostartFix"),
            "  DshTray.exe --lang-check — " + Loc.T("cli.help.langCheck"),
            "  DshTray.exe --layout-check — " + Loc.T("cli.help.layoutCheck"),
            "  DshTray.exe --selftest [--out <file>] — " + Loc.T("cli.help.selftest"),
            "  DshTray.exe --server-start [--open] — " + Loc.T("cli.help.serverStart"),
            "  DshTray.exe --server-restart [--open] — " + Loc.T("cli.help.serverRestart"),
            "  DshTray.exe --server-stop — " + Loc.T("cli.help.serverStop"),
            "  DshTray.exe --icons <file> — " + Loc.T("cli.help.icons"),
            "  DshTray.exe --shot <file> — " + Loc.T("cli.help.shot"),
            "  DshTray.exe --wait <url> — " + Loc.T("cli.help.wait"),
            "  DshTray.exe --pricing-check [--from <file>] [--apply] — " + Loc.T("cli.help.pricing"),
            "  DshTray.exe --backup [--full] — " + Loc.T("cli.help.backup"),
            "  DshTray.exe --backup-check [--from <file>] — " + Loc.T("cli.help.backupCheck"),
            "  DshTray.exe --restore [--from <file>] [--keys] [--no-engine] [--no-safety] [--no-settings] — " + Loc.T("cli.help.restore"),
            "  DshTray.exe --update-check [--out <file>] — " + Loc.T("cli.help.updateCheck"),
            "  DshTray.exe --update-prepare [--force] [--out <file>] — " + Loc.T("cli.help.updatePrepare"),
            "  DshTray.exe --node-check [--out <file>] — " + Loc.T("cli.help.nodeCheck"),
            "  DshTray.exe --install-node [--out <file>] — " + Loc.T("cli.help.installNode"),
            "",
            "  --port N — " + Loc.T("cli.help.port"),
            "  --lang ru|en|zh — " + Loc.T("cli.help.lang"),
            "  --out <file> — " + Loc.T("cli.help.out"),
            "  --from <file> — " + Loc.T("cli.help.from"),
            "  --apply — " + Loc.T("cli.help.apply"),
            "",
            Loc.T("cli.help.footer"),
            "",
        };

        return string.Join(Environment.NewLine, lines);
    }
}
