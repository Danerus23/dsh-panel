using System.Diagnostics;
using Microsoft.Win32;

namespace DshTray;

/// <summary>
/// Автозапуск панели при входе в Windows (HKCU\...\Run). Запись называется DSHPanel,
/// прежнее имя (DeepSeekHarness) читается и переносится на своё.
///
/// Запись хранит АБСОЛЮТНЫЙ путь к exe, поэтому её недостаточно прочитать — её надо сверить
/// с текущим расположением панели. Копий на машине может быть несколько (установленная,
/// портативная, папка сборки), папку могут перенести, а прежняя генерация пишет своё имя.
/// Панель, которая этого не делает, после входа в Windows поднимает ЧУЖУЮ (обычно старую)
/// копию и при этом показывает автозапуск включённым. Именно это и случилось у владельца:
/// в реестре осталась запись DeepSeekHarness на exe из папки сборки, а установленная панель
/// показывала «автозапуск включён» и не запускалась вовсе.
///
/// Правило починки при старте: запись под прежним именем переносим на себя; запись, ведущую
/// на отсутствующий файл или на копию СТАРЕЕ нашей, переводим на себя; запись на живую копию
/// той же или более новой версии не трогаем — две копии на машине это законная ситуация,
/// молча отбирать у соседа автозапуск нельзя, — но говорим о ней человеку (шарик и пункт меню).
///
/// Состояние для интерфейса читать через <see cref="Inspect"/>, а не через
/// <see cref="IsEnabled"/>: «включён» при записи на чужую копию — это и есть та путаница,
/// ради которой всё это написано. <see cref="IsEnabled"/> отвечает только на вопрос
/// «есть ли запись вообще» (по нему рисуется галочка «запускать при входе», потому что
/// автозапуск и правда включён — просто ведёт не туда).
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DSHPanel";
    private const string LegacyValueName = "DeepSeekHarness";
    private const string TrayArgument = "--tray";

    /// <summary>
    /// Ветка реестра для проверок: <c>DSH_PANEL_RUN_KEY</c> уводит чтение и запись автозапуска
    /// в свою ветку HKCU, чтобы проверка не трогала настоящий автозапуск человека.
    /// </summary>
    private static string KeyPath
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_PANEL_RUN_KEY");
            return string.IsNullOrWhiteSpace(custom) ? RunKeyPath : custom.Trim();
        }
    }

    /// <summary>Проверка увела запись автозапуска в свою ветку реестра.</summary>
    private static bool RunKeyOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_PANEL_RUN_KEY"));

    /// <summary>
    /// Это прогон проверки, а не работа панели владельца: у панели подменены свои каталоги или имя
    /// экземпляра, но ветка автозапуска не уведена в сторону. Так панель поднимают приёмка,
    /// проверка мастера и проверка замены файлов при обновлении, и такой прогон не имеет права
    /// править настоящий <c>HKCU\...\Run</c>.
    ///
    /// Это не теория: проверка замены файлов подняла тестовую панель из временной папки, та при
    /// старте «починила» боевую запись владельца и перевела её на свою временную копию — после
    /// перезагрузки вместо панели поднялась бы она, а потом папку удалили бы, и автозапуск молча
    /// перестал бы работать вовсе. Поэтому в таком прогоне починка не делает ничего.
    /// </summary>
    private static bool IsCheckRun =>
        !RunKeyOverridden
        && (HasEnv("DSH_PANEL_DATA") || HasEnv("DSH_PANEL_STATE") || HasEnv("DSH_PANEL_INSTANCE"));

    private static bool HasEnv(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    /// <summary>Куда ведёт запись автозапуска.</summary>
    public enum Where
    {
        /// <summary>Записи нет — автозапуск выключен.</summary>
        None,

        /// <summary>Запись ведёт на эту копию.</summary>
        Self,

        /// <summary>Запись ведёт на файл, которого на машине нет.</summary>
        Missing,

        /// <summary>Запись ведёт на другую копию панели, и она старее нашей.</summary>
        Older,

        /// <summary>Запись ведёт на другую живую копию той же или более новой версии.</summary>
        Other,

        /// <summary>Реестр не прочитался — состояние неизвестно, и трогать ничего нельзя.</summary>
        Unknown,
    }

    /// <summary>Что записано в реестре и куда это ведёт. Путь пуст, если записи нет.</summary>
    public readonly record struct State(Where Where, string Path, string EntryName, bool Legacy)
    {
        public bool Enabled => Where is Where.Self or Where.Older or Where.Other or Where.Missing;

        public static State Off => new(Where.None, "", "", false);

        public static State UnknownState => new(Where.Unknown, "", "", false);
    }

    /// <summary>Что сделала починка при старте — для журнала, шарика и проверок.</summary>
    public readonly record struct Fix(string Action, State Before, State After, bool Deduped)
    {
        public bool Changed => Action.Length > 0;

        /// <summary>Вторая запись (прежнее имя) была и убрана — об этом надо сказать отдельно.</summary>
        public bool HadDuplicate => Deduped;
    }

    /// <summary>Есть ли запись автозапуска вообще (по этому признаку рисуется галочка).</summary>
    public static bool IsEnabled() => Inspect().Enabled;

    /// <summary>
    /// Включает или выключает автозапуск ЭТОЙ копии. Выключение убирает оба имени: иначе
    /// в автозапуске осталась бы ссылка на прежнюю копию, а галочка показывала бы «выключено».
    /// </summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key == null) return false;

            if (enabled)
            {
                key.SetValue(ValueName, SelfValue());
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }

            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Переводит автозапуск на эту копию, что бы ни было записано. Нужно для случая, когда
    /// запись ведёт на живую соседнюю копию: панель не отбирает её молча, а предлагает это
    /// человеку пунктом меню.
    /// </summary>
    public static bool Retarget()
    {
        if (IsCheckRun) return false;

        WriteSelf();
        return Inspect().Where == Where.Self;
    }

    /// <summary>Читает запись автозапуска и говорит, куда она ведёт. Ничего не меняет.</summary>
    public static State Inspect()
    {
        var entry = Read(out var failed);
        if (failed) return State.UnknownState;
        if (entry == null) return State.Off;

        var (name, value, legacy) = entry.Value;
        var path = EntryPath(value);
        if (path.Length == 0) return new State(Where.Missing, "", name, legacy);

        // Сетевой путь из реестра не щупаем: File.Exists на нём может залипнуть на секунды
        // (отключённый диск, чужой узел), а панель в этот момент стоит. Такую запись не трогаем.
        if (IsNetworkPath(path)) return new State(Where.Other, path, name, legacy);

        if (!File.Exists(path)) return new State(Where.Missing, path, name, legacy);
        if (IsSelf(path)) return new State(Where.Self, path, name, legacy);

        return new State(Classify(path), path, name, legacy);
    }

    /// <summary>
    /// Чинит запись при старте панели. Возвращает совершённое действие (пустое — не трогали)
    /// и состояние до и после, чтобы вызывающий записал это в журнал и сказал человеку.
    /// </summary>
    public static Fix Repair()
    {
        // Прогон проверки с подменёнными каталогами настоящую запись владельца не трогает.
        if (IsCheckRun)
        {
            var checkState = Inspect();
            return new Fix("", checkState, checkState, false);
        }

        var before = Inspect();

        // Реестр не прочитался: не знаем, что там, и ничего не меняем.
        if (before.Where == Where.Unknown) return new Fix("", before, before, false);

        // Обе записи сразу: при входе поднимались бы две копии, и какая победит — дело случая.
        // Убрав прежнее имя, ОБЯЗАТЕЛЬНО разбираем оставшуюся запись дальше: если она ведёт на
        // мёртвый или старый файл, автозапуск остался бы сломанным, а починить его при следующем
        // входе было бы некому — сломанная запись ничего не запускает.
        var deduped = false;
        if (Raw(ValueName).Length > 0 && Raw(LegacyValueName).Length > 0)
        {
            DeleteValue(LegacyValueName);
            deduped = true;
        }

        var current = deduped ? Inspect() : before;
        var followed = RepairRemaining(current);

        // Если вторую запись убрали, а сама запись в порядке — это тоже изменение, о котором
        // надо сказать: иначе в журнале не осталось бы следа о снятом прежнем имени.
        var action = deduped && followed.Length == 0 ? "dedup" : followed;
        return new Fix(action, before, Inspect(), deduped);
    }

    /// <summary>Починка самой записи (когда вторая, если была, уже убрана).</summary>
    private static string RepairRemaining(State state)
    {
        switch (state.Where)
        {
            case Where.None:
            case Where.Unknown:
                return "";

            case Where.Self:
                // Путь верный, но значение могло остаться без --tray (тогда при входе открывается
                // окно панели вместо значка в трее) либо под прежним именем.
                if (!state.Legacy && HasTrayArgument(Raw(state.EntryName))) return "";
                return WriteResult(state.Legacy ? "migrated" : "normalized");

            case Where.Missing:
            case Where.Older:
                // Прежнее имя важнее причины: человеку надо знать, что запись прежней генерации
                // ушла, а куда она вела — видно в журнале рядом с путём «было: ...».
                var reason = state.Legacy ? "migrated" : state.Where == Where.Missing ? "missing" : "older";
                return WriteResult(reason);

            default:
                // Живая соседняя копия той же или более новой версии. Прежнее имя — исключение:
                // это имя прошлой генерации, и запись под ним переводим на себя всегда,
                // иначе старая копия останется в автозапуске навсегда.
                return state.Legacy ? WriteResult("migrated") : "";
        }
    }

    /// <summary>
    /// Пишет запись на себя и говорит, что получилось: если реестр не принял запись (нет прав,
    /// заперт политикой), это не успех — человеку и в журнал пойдёт «не удалось», а не «перевёл».
    /// </summary>
    private static string WriteResult(string action)
    {
        WriteSelf();
        return Inspect().Where == Where.Self ? action : "failed";
    }

    // --- реестр -------------------------------------------------------------

    /// <summary>Первая непустая запись: своё имя важнее прежнего.</summary>
    private static (string Name, string Value, bool Legacy)? Read(out bool failed)
    {
        failed = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key == null) return null;

            foreach (var (name, legacy) in new[] { (ValueName, false), (LegacyValueName, true) })
            {
                if (key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return (name, value, legacy);
                }
            }

            return null;
        }
        catch
        {
            // Реестр не прочитался — это НЕ «автозапуск выключен»: разница важна и для человека,
            // и для починки (при неизвестном состоянии мы ничего не трогаем).
            failed = true;
            return null;
        }
    }

    /// <summary>Значение записи как есть (пустая строка — записи нет).</summary>
    private static string Raw(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(name) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Пишет запись на эту копию и убирает прежнее имя.</summary>
    private static void WriteSelf()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key?.SetValue(ValueName, SelfValue());
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Не вышло: вызывающий увидит это, сверив состояние после записи (см. WriteResult).
        }
    }

    private static void DeleteValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // Не вышло — не повод падать: запись просто останется.
        }
    }

    // --- разбор значения ----------------------------------------------------

    /// <summary>Значение для записи: путь в кавычках (в пути бывает пробел) и ключ трея.</summary>
    private static string SelfValue() => $"\"{SelfExe()}\" {TrayArgument}";

    private static string SelfExe() => Environment.ProcessPath ?? Application.ExecutablePath;

    private static bool IsSelf(string path) =>
        Normalize(path).Equals(Normalize(SelfExe()), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Приводит путь к сравнимому виду: разворачивает относительные звенья, убирает хвостовой
    /// разделитель и точки. Без этого один и тот же файл, записанный по-разному (например через
    /// короткое имя папки), выглядел бы «другой копией».
    /// </summary>
    private static string Normalize(string path)
    {
        var text = (path ?? "").Trim();
        try
        {
            return Path.GetFullPath(text).TrimEnd('\\');
        }
        catch
        {
            return text.TrimEnd('\\');
        }
    }

    private static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Путь из значения записи: «"C:\...\DshTray.exe" --tray» или без кавычек.</summary>
    private static string EntryPath(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : "";
        }

        // Без кавычек: путь кончается на .exe, дальше могут идти ключи.
        var marker = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? text[..(marker + 4)] : text;
    }

    private static bool HasTrayArgument(string value) =>
        value.Contains(TrayArgument, StringComparison.OrdinalIgnoreCase)
        || value.Contains("--hidden", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Копия старее нашей или такая же/новее? Нечитаемая версия (файл не наша панель либо
    /// ресурс версии не читается) считается «старее»: запускать такую запись при входе
    /// человеку незачем.
    /// </summary>
    private static Where Classify(string path)
    {
        try
        {
            var theirs = Numeric(FileVersionInfo.GetVersionInfo(path).ProductVersion);
            if (theirs.Length == 0) return Where.Older;
            return UpdateService.IsNewer(AppVersion.Short, theirs) ? Where.Older : Where.Other;
        }
        catch
        {
            // Файл есть, но версию не прочитать: считаем запись бесполезной и переводим на себя.
            return Where.Missing;
        }
    }

    /// <summary>
    /// Числовая часть версии: «1.20.2+хеш коммита» → «1.20.2». То же правило, что и в
    /// UpdateService.Clean (он приватный): .NET дописывает в ProductVersion хеш сборки,
    /// и без обрезки сравнение версий не работает.
    /// </summary>
    private static string Numeric(string version)
    {
        var text = (version ?? "").Trim().TrimStart('v', 'V');
        var plus = text.IndexOf('+');
        if (plus > 0) text = text[..plus];
        return text.Trim();
    }
}
