using System.Globalization;
using DshPanel.Isolation;
using DshPanel.Server;
using DshPanel.Shell;

namespace DshPanel.Settings;

/// <summary>
/// ЧТО ПАНЕЛЬ ВИДИТ НА МАШИНЕ — ГРУППАМИ, А НЕ ПРОЗОЙ.
///
/// **Зачем это отдельным разбором.** Слова владельца 27.09.2026, а потом повторно 28.09.2026
/// (он прислал кадр раздела «Сервер»): *«я просил как то сделать здесь нормальную группировку
/// с указанием пути; так же нужно указать какая версия сейчас DSH, версия node и так далее»*.
/// Прежде отчёт был одной простынёй («движок: найден — …», «Node: …», «DSH_HOME: …») — в ней
/// не видно ни что искали, ни что нашли, ни где именно, а версий не было вовсе.
///
/// **Три части у каждой находки, и все три нужны:**
/// * <see cref="EnvironmentLine.What"/> — ЧТО искали (человеческими словами: «Движок DSH»);
/// * <see cref="EnvironmentLine.Result"/> — ЧТО нашли (версия — она и есть ответ «какая сейчас»);
/// * <see cref="EnvironmentLine.Where"/> — ГДЕ именно (путь), пусто — «где» не применимо.
///
/// ⚠️ **Путь показывается ДВУМЯ видами:** <see cref="EnvironmentLine.Where"/> — как его читает
/// человек, и <see cref="EnvironmentLine.WhereHidden"/> — то же с маской каталога пользователя
/// (<see cref="DisplayMask"/>). Второй нужен отчёту и журналу: имя пользователя в текст отчёта
/// не попадает (красная линия 7), а на экране путь показывается как есть — человеку он и нужен
/// целиком, чтобы его можно было скопировать в проводник. Решение «где маскировать» принято
/// ОДИН раз здесь, а не в каждом вызывающем.
///
/// ⚠️ **Ничего не выдумывается.** Нет движка — строка говорит «не найден» с причиной, а не
/// показывает пустое место; версия не прочиталась — так и сказано. Это правило панели: молчащая
/// строка читается как поломка, а выдуманное число — как проверенный факт.
/// </summary>
public sealed record EnvironmentLine(
    string What,
    string Result,
    string Where = "",
    string WhereHidden = "")
{
    /// <summary>Строка отчёта словами: «что искали → что нашли → где».</summary>
    public string Text() => Where.Length > 0
        ? $"{What}: {Result} — {WhereHidden}"
        : $"{What}: {Result}";
}

/// <summary>Группа находок: заголовок и строки под ним.</summary>
public sealed record EnvironmentGroup(string Title, IReadOnlyList<EnvironmentLine> Lines);

/// <summary>
/// ОТЧЁТ ОКРУЖЕНИЯ ЦЕЛИКОМ. Собирается ОДНИМ разбором (<see cref="EnvironmentReport.Build"/>) и
/// показывается ДВУМЯ способами: группами в разделе «Сервер» (<c>Views\SettingsWindow</c>) и
/// текстом в отчёте самотеста (<see cref="Text"/>). Второго способа собрать тот же отчёт быть
/// не должно: он бы разошёлся с первым, и человек читал бы одно, а проверка мерила другое.
/// </summary>
public sealed record EnvironmentReport(IReadOnlyList<EnvironmentGroup> Groups)
{
    /// <summary>Отчёт текстом — для журнала и для отчёта самотеста.</summary>
    public string Text()
    {
        var lines = new List<string>();

        foreach (var group in Groups)
        {
            lines.Add(group.Title);

            foreach (var line in group.Lines) lines.Add("    " + line.Text());
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Собрать отчёт. Всё, что нужно, приходит ПАРАМЕТРАМИ: разбор не знает ни о контроллере, ни
    /// о настройках, поэтому его можно прогнать на подставных значениях — и «нет движка»,
    /// и «движок есть, а Node не отвечает» проверяются без настоящей машины.
    /// </summary>
    /// <param name="engine">Найденные Node и движок; <c>null</c> — не нашли.</param>
    /// <param name="dshVersion">Версия движка (пусто — прочитать не удалось).</param>
    /// <param name="nodeVersion">Версия Node (пусто — спросить не удалось).</param>
    /// <param name="paths">Пути панели: свой корень, DSH_HOME, файл настроек.</param>
    /// <param name="workDir">Рабочая папка сервера, которая действует сейчас.</param>
    /// <param name="workDirConfigured">Она задана настройкой или взята по умолчанию.</param>
    /// <param name="serverPort">Порт, который панель займёт на самом деле.</param>
    /// <param name="serverText">Строка состояния сервера словами (её собирает контроллер).</param>
    public static EnvironmentReport Build(
        DshEngine? engine,
        string dshVersion,
        string nodeVersion,
        string npmVersion,
        string pnpmVersion,
        AppPaths paths,
        string workDir,
        bool workDirConfigured,
        int serverPort,
        string serverText)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var setup = new List<EnvironmentLine>();
        var runtime = new List<EnvironmentLine>();
        var dsh = new List<EnvironmentLine>();

        // --- ПАПКИ -------------------------------------------------------------------------

        // ⚠️ ПУТЬ ИДЁТ В `path:`, А НЕ В `Result:` — дефект, найденный 29.09.2026 самотестом
        // (`--settings-selftest`: «в отчёте нет имени пользователя» = False). Эти три строки клали
        // путь в `Result`, а `Result` в текстовый отчёт печатается КАК ЕСТЬ — маска применяется
        // только к `Where`. Из-за этого имя пользователя уезжало в текст отчёта, а отчёт человек
        // копирует в issue на GitHub (красная линия 7). На экране разницы не было: отрисовка
        // показывает путь из `Where` отдельной строкой моноширинным шрифтом, и он там и нужен —
        // целиком, чтобы его можно было скопировать в проводник.

        setup.Add(Line(
            PanelStrings.EnvDshHome,
            PanelStrings.EnvFound,
            found: true,
            path: paths.DshHome));

        setup.Add(Line(
            PanelStrings.EnvSettingsFile,
            PanelStrings.EnvFound,
            found: true,
            path: paths.SettingsFile));

        setup.Add(Line(
            PanelStrings.EnvBackupsDir,
            PanelStrings.EnvFound,
            found: true,
            path: paths.BackupsDir));

        // --- РАБОЧАЯ ПАПКА И ПОРТ ----------------------------------------------------------

        // Та же дверь, что у трёх папок выше: путь показывается столбцом «где», а не подставляется
        // в результат — иначе имя пользователя уезжало бы в текст отчёта. Заодно пропал повтор:
        // прежде путь печатался дважды в одной строке (результатом и «где»).
        setup.Add(Line(
            PanelStrings.EnvWorkDir,
            workDirConfigured ? PanelStrings.EnvFromSetting : PanelStrings.EnvFromPanel,
            found: true,
            path: workDir));

        setup.Add(Line(
            PanelStrings.EnvServerPort,
            Result: serverPort.ToString(CultureInfo.InvariantCulture),
            found: true));

        setup.Add(new EnvironmentLine(PanelStrings.EnvServerState, serverText));

        // --- ЧЕМ ПОДНИМАЮТ СЕРВЕР ----------------------------------------------------------

        runtime.Add(Line(
            PanelStrings.EnvNode,
            Result: nodeVersion.Length > 0 ? nodeVersion : PanelStrings.EnvFound,
            found: engine is not null,
            path: engine?.NodePath));

        var npmRoot = engine is null ? null : Backup.BackupPlanner.NpmDirectory(engine);

        runtime.Add(new EnvironmentLine(
            PanelStrings.EnvNpm,
            npmVersion.Length > 0
                ? npmVersion
                : (Directory.Exists(Path.Combine(npmRoot ?? string.Empty, "node_modules", "npm"))
                    ? PanelStrings.EnvFound
                    : PanelStrings.EnvNotFound),
            string.Empty,
            DisplayMask.Path(npmRoot ?? string.Empty)));

        runtime.Add(new EnvironmentLine(
            PanelStrings.EnvPnpm,
            pnpmVersion.Length > 0
                ? pnpmVersion
                : (Directory.Exists(Path.Combine(npmRoot ?? string.Empty, "node_modules", "pnpm"))
                    ? PanelStrings.EnvFound
                    : PanelStrings.EnvNotFound)));

        // --- САМ ДВИЖОК DSH ---------------------------------------------------------------

        dsh.Add(Line(
            PanelStrings.EnvEngine,
            Result: dshVersion.Length > 0 ? dshVersion : PanelStrings.EnvFound,
            found: engine is not null,
            path: engine?.BinPath));

        return new EnvironmentReport(new[]
        {
            new EnvironmentGroup(PanelStrings.EnvGroupSetup, setup),
            new EnvironmentGroup(PanelStrings.EnvGroupRuntime, runtime),
            new EnvironmentGroup(PanelStrings.EnvGroupDsh, dsh),
        });
    }

    /// <summary>
    /// Версия пакета по его каталогу — из его же <c>package.json</c>. Не прочиталась — пустая
    /// строка, и вызывающий скажет об этом словами: выдумывать номер нельзя.
    /// </summary>
    public static string VersionOf(string? packageDirectory) =>
        Platform.VersionProbe.FromPackage(packageDirectory);

    /// <summary>
    /// Строка находки. «Не нашли» и «нашли, но версии нет» — РАЗНЫЕ ответы, и оба называются
    /// словами: пустое место человек прочитал бы как поломку панели.
    /// </summary>
    private static EnvironmentLine Line(
        string what,
        string Result,
        bool found,
        string? path = null,
        string note = "")
    {
        var result = found
            ? (Result.Length > 0 ? Result : PanelStrings.EnvValueUnknown)
            : PanelStrings.EnvNotFound;

        if (note.Length > 0) result = $"{result} ({note})";

        var shown = found && !string.IsNullOrWhiteSpace(path) ? path! : string.Empty;

        return new EnvironmentLine(what, result, shown, DisplayMask.Path(shown));
    }
}
