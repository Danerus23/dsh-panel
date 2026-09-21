using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
                    foreach (var line in update.Notes.Replace("\r", "").Split('\n').Take(30)) report.AppendLine("  " + line);
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
            settings.UpdateNotes = update.Notes.Length > 8000 ? update.Notes[..8000] : update.Notes;
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

            var icons = new[] { ServerVisual.Running, ServerVisual.Stopped, ServerVisual.BusyOther };
            foreach (var visual in icons)
            {
                using var icon = TrayIconFactory.Create(visual, true, 32);
                using var offPeak = TrayIconFactory.Create(visual, false, 32);
                report.AppendLine($"Иконка {visual}: {icon.Size.Width}px (пик) и {offPeak.Size.Width}px (вне пика) — ок");
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
