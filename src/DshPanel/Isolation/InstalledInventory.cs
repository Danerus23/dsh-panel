using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DshPanel.Autostart;

namespace DshPanel.Isolation;

/// <summary>
/// Одна строка описи: что именно панель оставила на машине. <see cref="Kind"/> — из словаря видов
/// ниже, <see cref="Path"/> — путь файла, каталога, ярлыка или ветки реестра,
/// <see cref="Version"/> — версия для поставленного панелью (движок, свой Node); пусто у всего
/// остального.
/// </summary>
public sealed record InstalledEntry(string Kind, string Path, string Version = "");

/// <summary>
/// ОПИСЬ ТОГО, ЧТО ПАНЕЛЬ ПОСТАВИЛА НА МАШИНУ (этап D плана `ROADMAP-BORROWED.md`, п. 3).
///
/// **Зачем.** Удаление панели (этап G, деинсталлятор) обязано знать, что снимать: файлы, каталоги,
/// ветку реестра, ярлыки. Собирать это «по месту» в удаляющем коде — значит однажды забыть
/// половину и оставить на машине мусор, о котором человек не знает. Опись лежит РЯДОМ с остальным
/// состоянием панели (<c>state\installed.json</c>, путь даёт <see cref="AppPaths.InstalledFile"/>)
/// и пишется при обычном запуске человеком.
///
/// **Чего здесь НЕ записывается и почему.**
///
///   * **того, чего панель не ставила**: домашний каталог движка (<c>~/.dsh</c>) и дерево Node —
///     они не её, и красная линия запрещает их удалять. Опись, назвавшая их, однажды стала бы
///     списком на удаление;
///   * **того, чего нет на машине**: ярлыки попадают в опись, только если найдены на своих местах
///     (<see cref="ExistingShortcuts"/>). Установщик ставит их по задачам и умеет ставить
///     в общий рабочий стол вместо личного — поэтому мест несколько, и записывается найденное,
///     а не предполагаемое;
///   * **ветки реестра, когда автозапуск выключен**: запись в <c>Run</c> панель заводит только
///     по решению человека, и опись без записи — правда, а «на всякий случай» — выдумка.
///
/// **Имя продукта здесь не выдумывается** (см. <see cref="ProductName"/>): оно берётся у сборки,
/// а та же строка стоит в установщике (<c>#define AppName</c>) и в <c>&lt;Product&gt;</c> проекта.
/// Второй копии имени в коде нет — иначе ярлыки однажды искали бы по устаревшему имени.
/// </summary>
public static class InstalledInventory
{
    public const string KindDirectory = "каталог";
    public const string KindFile = "файл";
    public const string KindRegistry = "реестр";
    public const string KindShortcut = "ярлык";

    /// <summary>Поставленный панелью движок: папка версии. Уйдёт вместе с панелью.</summary>
    public const string KindEngine = "движок";

    /// <summary>
    /// СВОЙ Node панели (скачанный ею). **Системный Node сюда не попадает никогда** — он не наш,
    /// и красная линия запрещает его удалять: опись, назвавшая его, однажды стала бы списком
    /// на удаление.
    /// </summary>
    public const string KindNode = "Node";

    /// <summary>
    /// Имя продукта — как его зовёт человек. Берётся у СБОРКИ (<c>&lt;Product&gt;</c> проекта),
    /// а не пишется здесь строкой: установщик берёт имя из того же места (<c>#define AppName</c>
    /// в <c>installer\dsh-panel.iss</c>), и разойтись им нечем.
    ///
    /// Пусто — имя не прочиталось. Тогда ярлыки просто не ищутся: записать в опись путь,
    /// сложенный из пустого имени, значило бы соврать.
    /// </summary>
    public static string ProductName()
    {
        try
        {
            return Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyProductAttribute>()
                ?.Product
                ?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Где установщик ставит ярлыки панели. Мест четыре, и это не «на всякий случай»: Inno ставит
    /// ярлык на рабочий стол ПОЛЬЗОВАТЕЛЯ или на ОБЩИЙ (зависит от того, ставили ли для всех),
    /// а ярлык меню «Пуск» лежит либо в личной группе программ, либо в общей. Каталоги берутся
    /// у системы (<see cref="Environment.SpecialFolder"/>), а не складываются из строк руками.
    /// </summary>
    public static IReadOnlyList<string> ShortcutCandidates(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return Array.Empty<string>();

        var name = productName.Trim() + ".lnk";

        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", productName.Trim(), name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs", productName.Trim(), name),
        };
    }

    /// <summary>Ярлыки панели, которые НА САМОМ ДЕЛЕ есть на машине. Ответ «есть?» приходит
    /// аргументом — поэтому решение проверяется без диска.</summary>
    public static IReadOnlyList<string> ExistingShortcuts(string productName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        return ShortcutCandidates(productName).Where(exists).ToList();
    }

    /// <summary>
    /// Опись по фактам: каталоги и файлы панели (пути — из <see cref="AppPaths"/>, второго места,
    /// где они вычисляются, нет), ветка реестра — если автозапуск включён, ярлыки — какие нашлись,
    /// и **поставленное движком** (движок и свой Node — из записи
    /// <see cref="Server.EngineInstallRecord"/>, а не из имён папок).
    /// </summary>
    public static IReadOnlyList<InstalledEntry> Collect(
        AppPaths paths,
        string? panelExe,
        bool autostart,
        IReadOnlyList<string>? shortcuts,
        Server.EngineInstallRecord? engine = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var entries = new List<InstalledEntry>();

        void Add(string kind, string? path, string version = "")
        {
            if (!string.IsNullOrWhiteSpace(path)) entries.Add(new InstalledEntry(kind, path.Trim(), version));
        }

        Add(KindDirectory, paths.Root);
        Add(KindDirectory, paths.StateDir);
        Add(KindDirectory, paths.DataDir);
        Add(KindDirectory, paths.BackupsDir);
        Add(KindDirectory, paths.EngineDir);
        Add(KindDirectory, paths.NodeDir);

        Add(KindFile, panelExe);
        Add(KindFile, paths.SettingsFile);
        Add(KindFile, paths.LogFile);
        Add(KindFile, paths.EntryLinkFile);
        Add(KindFile, paths.OwnServerFile);
        Add(KindFile, paths.PricingHistoryFile);
        Add(KindFile, paths.InstalledFile);
        Add(KindFile, paths.EngineStateFile);

        // ПРОПУСК ТЕЛЕФОНА — наш файл (этап F), и он появится на машине, как только человек
        // включит телефон. В опись он входит ПУТЁМ и только путём: **содержимое не читается**,
        // потому что внутри пропуск, по которому калитка пускает устройство (красная линия 7).
        // Удаление панели (этап G) снимет файл по имени, не заглядывая внутрь.
        Add(KindFile, paths.PhonePassFile);

        // ПОСТАВЛЕННОЕ ДВИЖКОМ: движок — всегда, свой Node — только если он НАШ. Системный Node
        // в описи не появляется: он не наш, и удалять его панель не вправе.
        if (engine is not null && engine.Any)
        {
            Add(KindEngine, engine.EnginePath, engine.EngineVersion);

            if (engine.NodeOurs)
                Add(KindNode, engine.NodePath, engine.NodeVersion);
        }

        // Имя записи и ветка берутся у автозапуска, а не пишутся здесь строкой: одно место на всё.
        if (autostart)
            Add(KindRegistry, "HKCU\\" + AutostartDecisions.OwnerRunKeyPath + "\\" + AutostartDecisions.ValueName);

        foreach (var shortcut in shortcuts ?? Array.Empty<string>()) Add(KindShortcut, shortcut);

        return entries;
    }

    /// <summary>Опись как текст файла. Порядок строк задан сборкой — файл сравним и читаем глазами.</summary>
    public static string ToJson(IReadOnlyList<InstalledEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Записать опись под корнем панели. Возвращает признак успеха; исключений не бросает —
    /// неудавшаяся опись не имеет права уронить запуск панели.
    ///
    /// <paramref name="reason"/> — ТИП ошибки, а не её сообщение: в сообщениях .NET бывает путь
    /// с именем человека, а эта строка уходит в журнал (правило — личные данные не выносить).
    /// Типа хватает, чтобы понять класс беды (нет прав / диск занят / путь занят файлом).
    /// </summary>
    public static bool Write(
        AppPaths paths,
        string? panelExe,
        bool autostart,
        out string reason,
        out int entries,
        Func<string, bool>? exists = null,
        Server.EngineInstallRecord? engine = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        reason = string.Empty;
        entries = 0;

        try
        {
            var shortcuts = ExistingShortcuts(ProductName(), exists ?? File.Exists);
            var collected = Collect(paths, panelExe, autostart, shortcuts, engine);
            var text = ToJson(collected);

            Directory.CreateDirectory(paths.StateDir);
            File.WriteAllText(paths.InstalledFile, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            entries = collected.Count;
            return true;
        }
        catch (Exception error)
        {
            reason = error.GetType().Name;
            return false;
        }
    }
}
