using System.Globalization;
using System.Text;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Issue;

/// <summary>
/// ЧТО ПАНЕЛЬ ЗНАЕТ О СЕБЕ — ровно те факты, которые уходят в отчёт о проблеме.
///
/// **Зачем отдельный тип, а не готовый текст.** Требование владельца 29.09.2026: отчёт собирается
/// панелью, вычищается и показывается человеку <b>ровно в том виде, в каком уйдёт</b>
/// (<c>DESIGN.md</c> п. 38). Значит текст обязан быть ПОСТРОЕН, а не «собран по дороге»: только
/// так его можно показать до отправки, проверить и повторить. Здесь лежат факты, текст строит
/// <see cref="IssueReport"/>.
///
/// ⚠️ **Чего в этих фактах нет и быть не может:** ключа модели, баланса, ссылки входа
/// (в ней токен), содержимого <c>~/.dsh</c>, настроек человека, имён его копий. Это не «мы
/// постарались не включить» — их здесь НЕТ, и потому они не могут просочиться в текст.
/// </summary>
/// <param name="PanelVersion">Версия панели (<see cref="Views.AboutWindow.PanelVersion"/>).</param>
/// <param name="Revision">Ревизия сборки — хеш коммита из <c>ProductVersion</c>, если он есть.</param>
/// <param name="System">Версия Windows словами.</param>
/// <param name="EngineVersion">Версия движка DSH; пусто — не узнали.</param>
/// <param name="NodeVersion">Версия Node; пусто — не узнали.</param>
/// <param name="LaunchMode">Как панель запущена (человеком / прогон / изолированный прогон).</param>
/// <param name="ServerState">Состояние сервера: свой, найденный, порт занят, не работает.</param>
/// <param name="ServerPort">Порт сервера.</param>
/// <param name="LogTail">
/// Хвост журнала панели — УЖЕ вычищенный (<see cref="IssueReport"/> ничего не читает с диска;
/// строки приносит тот, у кого есть право читать журнал).
/// </param>
public sealed record IssueFacts(
    string PanelVersion,
    string Revision,
    string System,
    string EngineVersion,
    string NodeVersion,
    string LaunchMode,
    string ServerState,
    int ServerPort,
    IReadOnlyList<string> LogTail);

/// <summary>
/// ОТЧЁТ О ПРОБЛЕМЕ: собрать, вычистить, показать, отправить по нажатию.
///
/// **Чистое решение, а не окно.** Весь текст — здесь: его можно проверить прогоном (что в нём
/// есть, чего в нём НЕТ), а окно только показывает готовое. Порядок владельца (29.09.2026):
/// собрать → вычистить → показать ровно то, что уйдёт → отправить по нажатию. Панель сама
/// никуда ничего не отправляет: наружу уходит только <see cref="GitHubUrl"/> по щелчку человека.
///
/// ⚠️ **Две чистки, и обе обязательны** (красная линия 7):
/// * путь — <see cref="DisplayMask.Path"/>: каталог пользователя становится <c>~</c>;
/// * токен — здесь своя замена по шаблонам, потому что токенов в журнале может быть больше
///   видов, чем знает конкретный клиент: <c>token=…</c>, <c>key=…</c>, длинные строки вида
///   <c>sk-…</c> и <c>ghp_…</c>. Заменяем на <c>***</c>, а не вырезаем строку: строка журнала
///   нужна целиком, иначе отчёт теряет смысл.
/// </summary>
public static class IssueReport
{
    /// <summary>
    /// Слова: чего в отчёте нет и почему. Печатается рядом с текстом — человеку.
    ///
    /// ⚠️ Прежде это была КОНСТАНТА с русским текстом, и это был дефект, названный владельцем
    /// 29.09.2026: отчёт уезжает наружу, а на английской и китайской панели уезжал по-русски.
    /// Теперь строка живёт в словаре (<see cref="PanelStrings.IssueReportOmittedNote"/>) и берётся
    /// на языке панели; свойство оставлено, чтобы точки вызова не менялись.
    /// </summary>
    public static string OmittedNote => PanelStrings.IssueReportOmittedNote;

    /// <summary>
    /// СКОЛЬКО ЗНАКОВ ДОПУСТИМО В АДРЕСЕ GITHUB. Предел измерен случаем, а не выдуман: владелец
    /// 29.09.2026 нажал «Открыть на GitHub» и получил от GitHub «Whoa there! Your request URL is
    /// too long» — отчёт целиком (а в нём 40–60 строк журнала) в адрес не помещается, и кириллица
    /// в percent-кодировке стоит ШЕСТЬ знаков на букву. GitHub отказывает на адресе около 8 КБ,
    /// поэтому здесь запас: 7000.
    /// </summary>
    public const int MaxUrlLength = 7000;

    /// <summary>
    /// ТЕЛО ОТЧЁТА — И ОНО ЖЕ ТО, ЧТО УХОДИТ В ССЫЛКУ. Поэтому текст сам подбирает, сколько строк
    /// журнала в ссылку помещается, и **НАЗЫВАЕТ сокращение словами**: человек читает ровно то,
    /// что уйдёт (требование владельца, `DESIGN.md` п. 38), а не «примерно такое».
    ///
    /// ⚠️ **Почему порядок перебора именно такой.** Строки журнала отбрасываются С САМОГО СТАРОГО
    /// конца: причину неисправности почти всегда называют последние строки, а первые — это запуск
    /// панели и разведка портов. Свой текст человека не режется, пока в отчёт влезает хоть одна
    /// строка журнала; и только если не влезает даже он, режется и он — с пометкой в тексте.
    ///
    /// ⚠️ Чего здесь НЕ делается: двух текстов. Соблазн был — отдать в ссылку укороченный, а в файл
    /// полный; так текст в окне перестал бы быть тем, что уйдёт, и обещание п. 38 стало бы ложью.
    ///
    /// Тонкая дверь над <see cref="TextIn"/>: язык берётся у панели.
    /// </summary>
    public static string Text(IssueFacts facts, string? message = null) => TextIn(string.Empty, facts, message);

    /// <summary>
    /// ТЕЛО ОТЧЁТА НА НАЗВАННОМ ЯЗЫКЕ. Пустой <paramref name="language"/> — выбранный язык панели;
    /// «ru»/«en»/«zh» — язык явно, НЕ трогая выбор процесса.
    ///
    /// ⚠️ **Зачем отдельная дверь, а не только <see cref="Text"/>.** Отчёт — единственный текст
    /// панели, который уезжает НАРУЖУ (на GitHub, к людям), поэтому «на английском он английский»
    /// обязано быть проверяемым утверждением. А проверка не имеет права менять язык процесса:
    /// xunit гоняет классы параллельно, и выставленный в одном месте «en» покраснил бы соседний
    /// класс (та же беда, из-за которой заведён <c>Loc.TIn</c>). Поэтому язык здесь — ПАРАМЕТР.
    /// </summary>
    public static string TextIn(string? language, IssueFacts facts, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var wanted = language ?? string.Empty;

        for (var take = facts.LogTail.Count; take >= 1; take--)
        {
            var text = Build(wanted, facts, message, take, messageCut: false);
            if (Url(text).Length <= MaxUrlLength) return text;
        }

        // Журнал не помещается вовсе: тогда отчёт строится без его строк, и это тоже сказано.
        var withoutLog = Build(wanted, facts, message, 0, messageCut: false);
        if (Url(withoutLog).Length <= MaxUrlLength) return withoutLog;

        // И даже без журнала не влезает — значит длинный СВОЙ текст человека. Режем его, называя это.
        var said = (message ?? string.Empty).Trim();

        for (var keep = said.Length; keep > 0; keep -= 20)
        {
            // ⚠️ Пометка ставится только тогда, когда текст и вправду урезан: на первой итерации
            // он цел, и сказать «сокращён» значило бы соврать в самом отчёте.
            var cut = keep < said.Length;

            var text = Build(wanted, facts, said[..keep], 0, messageCut: cut);
            if (Url(text).Length <= MaxUrlLength) return text;
        }

        // Вырожденный случай (пустой текст человека тоже не влезает): отчёт отдаётся как есть —
        // его факты одни весят около тысячи знаков, и в предел они укладываются с большим запасом.
        // Что это так, стережёт проверка `Отчёт_без_журнала_и_без_слов_человека_влезает_в_ссылку`.
        return Build(wanted, facts, string.Empty, 0, messageCut: true);
    }

    /// <summary>
    /// СТРОКА СЛОВАРЯ ПО ИМЕНИ ЧЛЕНА <see cref="PanelStrings"/>: пустой язык — выбранный панелью,
    /// названный — явно и без правки состояния процесса.
    ///
    /// Одна дверь на весь отчёт намеренно: второе место, где строка берётся по-своему, разошлось бы
    /// с первым ровно так же, как разошёлся прежний русский литерал с английским словарём.
    /// </summary>
    private static string S(string language, string key) =>
        language.Length == 0 ? Loc.T(key) : Loc.TIn(language, key);

    /// <summary>
    /// Собрать текст: <paramref name="language"/> — язык отчёта (пусто — язык панели),
    /// <paramref name="take"/> — сколько ПОСЛЕДНИХ строк журнала показать (0 — ни одной),
    /// <paramref name="messageCut"/> — сказать ли, что текст человека сокращён.
    /// </summary>
    private static string Build(string language, IssueFacts facts, string? message, int take, bool messageCut)
    {
        var text = new StringBuilder();

        var said = (message ?? string.Empty).Trim();
        text.AppendLine("### " + S(language, nameof(PanelStrings.IssueReportHeadingWhat)));
        text.AppendLine();
        text.AppendLine(said.Length > 0 ? said : S(language, nameof(PanelStrings.IssueReportMessageHint)));

        if (messageCut) text.AppendLine(S(language, nameof(PanelStrings.IssueMessageTrimmed)));

        text.AppendLine();
        text.AppendLine("### " + S(language, nameof(PanelStrings.IssueReportHeadingPanel)));
        text.AppendLine();
        Line(text, language, S(language, nameof(PanelStrings.IssueReportVersion)), Version(language, facts));
        Line(text, language, "Windows", Clean(facts.System));
        Line(text, language, S(language, nameof(PanelStrings.IssueReportEngine)), Unknown(language, facts.EngineVersion));
        Line(text, language, "Node", Unknown(language, facts.NodeVersion));
        Line(text, language, S(language, nameof(PanelStrings.IssueReportLaunch)), Clean(facts.LaunchMode));
        Line(text, language, S(language, nameof(PanelStrings.IssueReportServer)), Clean(facts.ServerState) +
            (facts.ServerPort > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    S(language, nameof(PanelStrings.IssueReportPortFormat)),
                    facts.ServerPort)
                : string.Empty));

        text.AppendLine();
        text.AppendLine("### " + S(language, nameof(PanelStrings.IssueReportHeadingLog)));
        text.AppendLine();
        text.AppendLine("`");

        var shown = facts.LogTail.Skip(Math.Max(0, facts.LogTail.Count - take)).ToList();

        if (shown.Count == 0)
        {
            // Пустой блок ` ` читается как поломка окна. Слова нужны и здесь: «журнал пуст
            // или недоступен» — это ответ, а пустое место — нет. Нашлось на кадре.
            text.AppendLine(S(language, nameof(PanelStrings.IssueNoLog)));
        }
        else
        {
            foreach (var line in shown) text.AppendLine(Clean(line));
        }

        if (shown.Count < facts.LogTail.Count)
        {
            text.AppendLine(string.Format(
                CultureInfo.CurrentCulture,
                S(language, nameof(PanelStrings.IssueLogTrimmedFormat)),
                shown.Count,
                facts.LogTail.Count));
        }

        text.AppendLine("`");

        text.AppendLine();
        text.AppendLine("---");

        // ⚠️ ОГОВОРКА О ЯЗЫКЕ ЖУРНАЛА — на КАЖДОМ языке и ДО слов о том, чего в отчёте нет.
        // Строки журнала панель ведёт по-русски осознанно и не переводит, поэтому отчёт обязан
        // сказать это словами: иначе русские строки на английском отчёте читаются как поломка.
        text.AppendLine(S(language, nameof(PanelStrings.IssueReportLogRussianNote)));
        text.AppendLine(S(language, nameof(PanelStrings.IssueReportOmittedNote)));

        return text.ToString();
    }

    /// <summary>
    /// Ссылка на страницу НОВОГО выпуска проблемы с заполненным текстом. Адрес — тот же
    /// репозиторий продукта (<see cref="ProductLinks"/>), не второй.
    ///
    /// Текст берётся у <see cref="Text"/> — то есть тот самый, что человек видит в предпросмотре:
    /// ссылка и окно не могут разойтись по построению.
    /// </summary>
    public static string GitHubUrl(IssueFacts facts, string? message = null) => Url(Text(facts, message));

    /// <summary>Адрес страницы нового выпуска с этим телом — одна дверь на все случаи.</summary>
    private static string Url(string body) =>
        ProductLinks.IssuesNewUrl + "?body=" + Uri.EscapeDataString(body);

    /// <summary>
    /// Вычистить ОДНУ строку: путь замаскировать, токен заменить. Отдельной дверью, потому что
    /// чистить надо и хвост журнала, и любое поле — а второй реализации чистки быть не должно.
    /// </summary>
    public static string Clean(string? line)
    {
        var text = line ?? string.Empty;
        if (text.Length == 0) return string.Empty;

        text = MaskPaths(text);
        text = Token(text);

        return text;
    }

    /// <summary>
    /// Замаскировать каталоги пользователя ВНУТРИ строки, а не только в её начале.
    ///
    /// ⚠️ Здесь была ДЫРА, и нашла её проверка, а не глаз: <see cref="DisplayMask.Path"/> заменяет
    /// каталог, только если строка НАЧИНАЕТСЯ с него (или равна ему) — а журнал панели полон строк
    /// вида «рабочая папка сервера: C:\Users\кто-то\работа». Такие пути ушли бы в отчёт целыми,
    /// вместе с именем пользователя, то есть ровно то, что красная линия 7 запрещает.
    ///
    /// Своих шаблонов два, и оба нужны: каталог ПОЛЬЗОВАТЕЛЯ этой машины (его знает
    /// <see cref="Environment.SpecialFolder.UserProfile"/>) и каталог ЛЮБОГО другого пользователя
    /// (<c>диск:\Users\имя</c>) — второй остаётся в журнале от чужой копии или из отчёта движка.
    /// </summary>
    private static string MaskPaths(string text)
    {
        var masked = text;

        var profile = System.IO.Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        if (!string.IsNullOrWhiteSpace(profile))
        {
            masked = System.Text.RegularExpressions.Regex.Replace(
                masked,
                System.Text.RegularExpressions.Regex.Escape(profile) + @"[\\/]?",
                "~",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return System.Text.RegularExpressions.Regex.Replace(
            masked,
            @"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""']+[\\/]?",
            "~",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Заменить секреты на <c>***</c>. Шаблоны узкие и с ПРИЧИНОЙ на каждом:
    /// <c>token</c> и <c>key</c> со знаком «=» или «:» — так их пишет и панель, и движок;
    /// <c>sk-</c> и <c>gh[pousr]_</c> — узнаваемые начала ключей. Широкое «любые 20 знаков
    /// подряд» вычистило бы половину журнала и сделало отчёт бесполезным.
    /// </summary>
    private static string Token(string text)
    {
        var withPairs = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"((?:token|key|secret|password)\s*[=:]\s*)([^\s,;""']+)",
            "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return System.Text.RegularExpressions.Regex.Replace(
            withPairs,
            @"\b(sk-[A-Za-z0-9_\-]{8,}|gh[pousr]_[A-Za-z0-9]{8,})\b",
            "***");
    }

    /// <summary>Версия панели с ревизией, если ревизия известна.</summary>
    private static string Version(string language, IssueFacts facts) =>
        facts.Revision.Length > 0
            ? facts.PanelVersion + " (" + facts.Revision + ")"
            : facts.PanelVersion.Length > 0
                ? facts.PanelVersion
                : S(language, nameof(PanelStrings.IssueReportVersionUnknown));

    /// <summary>
    /// Пустая версия — НЕ пустое место, а сказанный словами ответ: «не удалось узнать» на языке
    /// отчёта. Пустое место человек прочитал бы как «движка нет вовсе» и пошёл бы искать не то.
    /// </summary>
    private static string Unknown(string language, string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? S(language, nameof(PanelStrings.IssueReportUnknown))
            : Clean(value);

    /// <summary>
    /// Одна строка раздела «Панель»: подпись, двоеточие и значение. Пустое значение заменяется
    /// знаком из словаря, а не выдуманным текстом — и тоже на языке отчёта.
    /// </summary>
    private static void Line(StringBuilder text, string language, string what, string value) =>
        text.AppendLine("- **" + what + ":** " +
            (value.Length > 0 ? value : S(language, nameof(PanelStrings.IssueReportEmptyValue))));
}
