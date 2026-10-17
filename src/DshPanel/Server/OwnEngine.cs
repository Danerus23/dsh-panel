namespace DshPanel.Server;

/// <summary>
/// СВОЙ ДВИЖОК: где он лежит и как узнать его **по пути запуска**.
///
/// **Зачем это отдельно от приметы по командной строке.** Сегодня «наш ли это сервер» решается
/// формой командной строки (<c>…@deepseek-ai\dsh\lib\bin.js</c> + <c>web</c> + порт) и записью
/// <c>state\own-server.txt</c> (порт; PID; время создания). После этапа E3 панель ставит **свой**
/// движок в **свою** папку и обновляет его — и тогда:
///
/// * движок у человека и движок панели могут быть **разными версиями** одного и того же пакета,
///   а форма командной строки у них одинаковая: по ней «наш» от «чужого глобального» не отличить;
/// * **обновление движка порождает НОВЫЙ процесс** — другой номер и другое время создания.
///   Прежняя запись перестаёт совпадать, и панель приняла бы **свой же** сервер за найденный чужой:
///   предложила бы «встроиться» и отказывалась бы его гасить.
///
/// Поэтому владение получает **третью примету — путь запуска** (решение владельца 10.10.2026, план
/// E3.0): сервер поднят из **нашей** папки движка (`<корень панели>\engine\<версия>\…`) — значит он
/// наш, чей бы номер процесса ни был.
///
/// Три случая, которые различает панель, и все три названы словами:
///
/// | Случай | Как узнаётся | Что делает панель |
/// |---|---|---|
/// | **наш** | путь запуска — в нашей папке движка **или** совпала запись (PID + время) | гасит свободно, это её процесс |
/// | **чужой глобальный** | движок вне нашей папки (установка человека) | показывает и спрашивает разрешение |
/// | **чужой, взятый под управление** | ответ человека запомнен (`Adopted`) | гасит свободно после запомненного согласия |
///
/// ⚠️ **Только чтение и только сравнение.** Ни один метод здесь ничего не создаёт и не удаляет:
/// папку движка заводит установщик (`E3.1`), а не примета владения.
/// </summary>
public static class OwnEngine
{
    /// <summary>
    /// ПАПКА ВЕРСИИ своего движка: <c>&lt;папка движка&gt;\&lt;версия&gt;</c> — одна дверь для
    /// установщика (E3.1) и для приметы владения. Пустая версия папки не даёт.
    /// </summary>
    public static string VersionDir(string? engineDir, string? version)
    {
        if (string.IsNullOrWhiteSpace(engineDir) || string.IsNullOrWhiteSpace(version)) return string.Empty;

        return Path.Combine(engineDir.Trim(), version.Trim());
    }

    /// <summary>
    /// Путь к <c>bin.js</c> СВОЕГО движка: <c>&lt;папка движка&gt;\&lt;версия&gt;\node_modules\@deepseek-ai\dsh\lib\bin.js</c>.
    ///
    /// Версия — **имя папки**, а не запись в настройках: так две версии не смешиваются, а откат
    /// (E3.3) — это просто «подними сервер из соседней папки». Пустая версия пути не даёт:
    /// <c>&lt;папка движка&gt;\node_modules\…</c> было бы движком «вообще», без версии.
    /// </summary>
    public static string BinPath(string? engineDir, string? version)
    {
        var folder = VersionDir(engineDir, version);
        if (folder.Length == 0) return string.Empty;

        return Path.Combine(folder, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    }

    /// <summary>
    /// Лежит ли движок в НАШЕЙ папке — по пути запуска.
    ///
    /// ⚠️ Сверка идёт **по границе имени папки**, а не по вхождению подстроки: `…\engine-чужой\…`
    /// не должен считаться нашим только потому, что начинается похоже (тот же урок, что с
    /// `…\lib\bin.js.old` в примете по командной строке, найденный аудитом v1).
    /// </summary>
    public static bool IsOwnBinPath(string? binPath, string? engineDir)
    {
        var path = Normalize(binPath);
        var root = Normalize(engineDir);

        if (path.Length == 0 || root.Length == 0) return false;

        // Свой путь — ИЛИ сама папка движка, ИЛИ что-то ВНУТРИ неё (с разделителем на границе).
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ВЕРСИЯ ИЗ ПАПКИ своего движка (пусто — путь не наш или версии в пути нет).
    /// Это та же версия, что показывает опись движка, но добытая **без чтения файлов** — по имени
    /// папки, которую завела панель. Нужна для записи «какой движок поднялся» и для отката.
    /// </summary>
    public static string VersionFolder(string? binPath, string? engineDir)
    {
        if (!IsOwnBinPath(binPath, engineDir)) return string.Empty;

        var path = Normalize(binPath);
        var root = Normalize(engineDir);

        var tail = path.Length > root.Length ? path[(root.Length + 1)..] : string.Empty;
        if (tail.Length == 0) return string.Empty;

        var separator = tail.IndexOf(Path.DirectorySeparatorChar);
        var folder = separator < 0 ? tail : tail[..separator];

        return folder.Trim();
    }

    /// <summary>
    /// Путь к <c>bin.js</c> ИЗ КОМАНДНОЙ СТРОКИ процесса. Пусто — в строке движка нет.
    ///
    /// Разбор аргументов берётся у приметы владения (<see cref="ServerDecisions.Arguments"/>),
    /// а «похоже ли на движок» — у <see cref="ServerDecisions.LooksLikeDshBin"/>: второго разбора
    /// командной строки в панели быть не должно, иначе однажды два места разойдутся.
    /// </summary>
    public static string BinPathFromCommandLine(string? commandLine)
    {
        foreach (var argument in ServerDecisions.Arguments(commandLine))
        {
            if (ServerDecisions.LooksLikeDshBin(argument)) return argument.Trim().Trim('"');
        }

        return string.Empty;
    }

    /// <summary>
    /// Версия ПАКЕТА движка, лежащего по этому пути: читается из его же <c>package.json</c>.
    /// Пусто — не прочиталась. Отдельно от <see cref="VersionFolder"/>: папку мог завести человек,
    /// и имя папки — не доказательство версии; доказательство — то, что движок о себе говорит.
    /// </summary>
    public static string VersionOf(string? binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath)) return string.Empty;

        var packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(binPath.Trim()));
        if (string.IsNullOrEmpty(packageRoot)) return string.Empty;

        return EngineRpcDeclarations.PackageVersion(Path.Combine(packageRoot, "package.json"));
    }

    /// <summary>
    /// Путь к своему движку для ЗАПИСИ о своём сервере и для отчёта: он либо из папки движка
    /// (когда панель его знает), либо из командной строки живого процесса.
    /// </summary>
    public static string Describe(string? binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath)) return string.Empty;
        return binPath.Trim();
    }

    /// <summary>
    /// ЗАПИСЬ «ЭТОТ СЕРВЕР НАШ» С НАЗВАННЫМ ДВИЖКОМ — одна дверь на оба случая: сервер только что
    /// поднят панелью и сервер **узнан по пути движка** после обновления.
    ///
    /// Второй сборки этой записи быть не должно: два места однажды разошлись бы, и в одном из них
    /// движок остался бы не назван — а тогда панель после обновления не сказала бы, что работает,
    /// и откат (E3.3) не знал бы, куда возвращаться.
    /// </summary>
    public static OwnServerRecord Record(int port, int pid, long startTicks, string? engineBinPath) =>
        new(port, pid, startTicks, Describe(engineBinPath), VersionOf(engineBinPath));

    /// <summary>
    /// КАКОЙ ДВИЖОК ПОДНИМАТЬ ПАНЕЛИ (пункт 7 плана E3.2): **сначала СВОЙ поставленный**, потом
    /// уже установленный у человека.
    ///
    /// **Зачем это здесь, а не в `App`.** Раньше панель поднимала движок **только** по общим местам
    /// (`DshEngine.Locate`: PATH, глобальная установка npm). После установки своего движка (E3.1)
    /// этого мало: панель поставила версию в СВОЮ папку и обязана поднимать сервер **с неё** —
    /// иначе обновление (E3.2) ставило бы версию, которую панель тут же перестала бы использовать,
    /// и «обновил» означало бы «скачал в сторонку».
    ///
    /// **Три правила, и все три — из границ проекта:**
    ///
    /// 1. **Своя версия берётся ТОЛЬКО из записи** (`state\engine.json`), а не из имён папок: папку
    ///    мог завести человек, и поднимать из неё движок значило бы признать чужую установку своей.
    ///    Запись говорит и **каким Node** панель его ставила; если того Node больше нет — ищем Node
    ///    заново, но движок остаётся своим.
    /// 2. **Подмена окружением главнее** (`DSH_PANEL_NODE` / `DSH_PANEL_ENGINE`): на ней стоят
    ///    прогоны проверок (`--engine-install-selftest`, `--server-selftest` с подставным движком);
    ///    если она есть — панель верит ей, как верила раньше.
    /// 3. **Запись есть, а папки нет — не беда, а откат к общему месту:** панель не отказывается
    ///    поднимать сервер из-за расхождения в файле состояния (запись могли унести вместе с папкой).
    ///    Это НЕ тихая подмена: путь поднятого движка панель показывает и записывает, как и раньше.
    ///
    /// ⚠️ Здесь **только чтение**: ни одной записи, ни одного удаления — папку заводит установщик.
    /// </summary>
    public static DshEngine? Locate(Isolation.AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // (2) Подмена окружением — как было: ею живут проверки с подставным движком.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DshEngine.EngineOverrideVariable)))
            return DshEngine.Locate();

        // (1) Своя поставленная версия — из ЗАПИСИ, а не из имён папок.
        var record = EngineInstallRecord.Read(paths.EngineStateFile);
        if (record.Any)
        {
            var bin = BinPath(paths.EngineDir, record.EngineVersion);

            if (bin.Length > 0 && File.Exists(bin))
            {
                var node = record.NodePath.Length > 0 && File.Exists(record.NodePath)
                    ? record.NodePath
                    : DshEngine.FindNode();

                if (!string.IsNullOrWhiteSpace(node)) return new DshEngine(node, bin);
            }
        }

        // (3) Своего нет (или он пропал) — общая установка человека, как раньше.
        return DshEngine.Locate();
    }

    /// <summary>Канонический вид пути: полный, без хвостового разделителя, с прямыми разделителями.</summary>
    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var text = path.Trim().Trim('"');

        try
        {
            text = Path.GetFullPath(text);
        }
        catch
        {
            // Путь не разобрался (мусор в файле записи) — сравниваем как есть: сравнение
            // по границе папки всё равно не совпадёт, а падать из-за строки в файле нельзя.
        }

        return Path.TrimEndingDirectorySeparator(text);
    }
}
