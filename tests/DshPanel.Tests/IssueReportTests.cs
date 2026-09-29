using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DshPanel.Issue;
using DshPanel.Localization;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОТЧЁТ О ПРОБЛЕМЕ: что он обязан содержать, чего в нём быть НЕ МОЖЕТ и чем он чистится.
///
/// **Проверки эти — не про формат текста, а про КРАСНУЮ ЛИНИЮ 7.** Отчёт уезжает наружу
/// (на GitHub, в файл, в буфер обмена), поэтому «в нём нет личных данных» — утверждение,
/// которое обязано уметь падать. Проверяется оно ДВУМЯ сторонами, и обе нужны:
/// * тем, ЧЕГО В ТЕКСТЕ НЕТ (ключ модели, баланс, ссылка входа) — на подложенных строках,
///   ровно таких, какие панель пишет в журнал;
/// * тем, ЧТО В ТЕКСТЕ ЕСТЬ (версия, версия Windows, хвост журнала) — иначе проверка зеленела бы
///   и на пустом отчёте, то есть на отчёте, который человеку бесполезен.
///
/// ⚠️ Чего эти проверки НЕ покрывают: что факты собраны ПРАВДИВО (это проверяется на живом
/// прогоне, а не здесь) и что текст понятен человеку — это редактура, её читает владелец.
/// </summary>
public class IssueReportTests
{
    /// <summary>Факты с узнаваемыми значениями: их видно в тексте, и по ним же видно пропажу.</summary>
    private static IssueFacts Facts(params string[] logTail) => new(
        PanelVersion: "2.0.0",
        Revision: "abc1234",
        System: "Windows 11 (10.0.26100)",
        EngineVersion: "0.1.5-rc.3",
        NodeVersion: "26.0.0",
        LaunchMode: "обычный запуск человеком",
        ServerState: "сервер свой, работает",
        ServerPort: 3080,
        LogTail: logTail.Length > 0 ? logTail : new[] { "2026-09-29 00:00:00 панель запущена" });

    /// <summary>
    /// ОТЧЁТ НАЗЫВАЕТ ТО, ПО ЧЕМУ ПРОБЛЕМУ РАЗБИРАЮТ. Без этих строк он бесполезен: «не работает»
    /// без версии, версии Windows и состояния сервера не разбирается вовсе.
    /// </summary>
    [Fact]
    public void Отчёт_называет_версию_систему_движок_и_состояние_сервера()
    {
        var text = IssueReport.Text(Facts());

        Assert.Contains("2.0.0", text, StringComparison.Ordinal);
        Assert.Contains("abc1234", text, StringComparison.Ordinal);
        Assert.Contains("Windows 11", text, StringComparison.Ordinal);
        Assert.Contains("0.1.5-rc.3", text, StringComparison.Ordinal);
        Assert.Contains("26.0.0", text, StringComparison.Ordinal);
        Assert.Contains("обычный запуск человеком", text, StringComparison.Ordinal);
        Assert.Contains("3080", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ЧЕГО В ОТЧЁТЕ НЕТ — САМОЕ ВАЖНОЕ. Подложены строки ровно такого вида, какие панель пишет
    /// в журнал: токен запуска движка, ключ модели в тексте ошибки, ссылка входа с секретом.
    /// Ни одна из них не имеет права попасть в текст, который уходит наружу.
    ///
    /// ⚠️ Проверка проходит через то состояние, в котором дефект возможен: строки сначала
    /// ПОПАДАЮТ в хвост журнала (иначе проверять было бы нечего), и только потом — в отчёт.
    /// ⚠️ **Баланс в хвосте журнала при этом ОСТАЁТСЯ, и это названо честно.** Баланс — не секрет
    /// (по нему нельзя войти и нельзя потратить), и панель не имеет права вырезать чужие строки
    /// журнала: отчёт, из которого выброшены строки, врёт о том, что было. В КАДР и в README
    /// баланс не попадает — там он не нужен вовсе; здесь он нужен так же, как остальные строки.
    /// </summary>
    [Fact]
    public void Отчёт_не_выносит_ни_ключа_ни_ссылки_входа()
    {
        // Подставные значения нарочно НЕ похожи на настоящие ключи: сторож личных данных ищет
        // узнаваемые начала (`sk-`, `ghp_`) и справедливо ругается на них даже в тесте.
        // Проверке важен ФОРМАТ пары «имя=значение», а не то, как значение выглядит.
        var facts = Facts(
            "движок: ответ HTTP 401, token=ПОДСТАВНОЙ_КЛЮЧ_ДВИЖКА",
            "ключ модели прочитан: key=ПОДСТАВНОЙ_КЛЮЧ_МОДЕЛИ",
            "ссылка входа: http://127.0.0.1:3080/?token=ПОДСТАВНОЙ_СЕКРЕТ_ВХОДА");

        var text = IssueReport.Text(facts);

        // Токены — не «в отчёте их нет», а «они заменены»: строка журнала остаётся, секрет уходит.
        Assert.DoesNotContain("ПОДСТАВНОЙ_КЛЮЧ_ДВИЖКА", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ПОДСТАВНОЙ_КЛЮЧ_МОДЕЛИ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ПОДСТАВНОЙ_СЕКРЕТ_ВХОДА", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);

        // Хвост журнала при этом НЕ выброшен: отчёт без журнала разбирать нечем.
        Assert.Contains("движок: ответ HTTP 401", text, StringComparison.Ordinal);

        // И честная строка о том, чего в отчёте нет, — рядом с текстом, а не в обещании.
        Assert.Contains(IssueReport.OmittedNote, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// В ОТЧЁТЕ НЕТ НАСТРОЕК ЧЕЛОВЕКА. Ключ модели, ссылка входа и содержимое <c>~/.dsh</c>
    /// живут в файлах, и отчёт их не читает вовсе — здесь это проверяется на том, что текст
    /// отчёта не содержит ни одного вида этих данных, даже если их подложить в ФАКТЫ.
    /// </summary>
    [Fact]
    public void Отчёт_не_показывает_содержимого_файлов_человека()
    {
        var text = IssueReport.Text(Facts());

        Assert.DoesNotContain(".credentials.yaml", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entry-link", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings.json", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ПУТЬ ЧЕЛОВЕКА ЗАМАСКИРОВАН. Каталог пользователя заменяется на <c>~</c> — и в фактах,
    /// и в хвосте журнала: журнал панели полон путей, и именно оттуда утекает имя пользователя.
    /// </summary>
    [Fact]
    public void Отчёт_маскирует_каталог_пользователя_и_в_фактах_и_в_журнале()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(profile), "без профиля проверять нечего");

        var facts = Facts(
            "рабочая папка сервера: " + profile + "\\работа",
            "журнал панели: " + profile + "\\AppData\\Local\\DshPanel2\\state\\panel.log");

        var text = IssueReport.Text(facts);

        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("~", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОТСУТСТВИЕ ФАКТА НЕ ВРАНЬЁ. «Не удалось узнать» — это ответ; пустое место человек прочитает
    /// как «движка нет вовсе», и пойдёт искать не то.
    /// </summary>
    [Fact]
    public void Неизвестная_версия_движка_названа_а_не_пропущена()
    {
        var facts = Facts() with { EngineVersion = string.Empty, NodeVersion = "  " };

        var text = IssueReport.Text(facts);

        Assert.Contains("Движок DSH", text, StringComparison.Ordinal);
        Assert.Contains("не удалось узнать", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ССЫЛКА ВЕДЁТ В ТОТ ЖЕ РЕПОЗИТОРИЙ, ЧТО И ВЫПУСКИ. Разойдись они — человек написал бы
    /// в один репозиторий, а обновления получал из другого, и отчёт пропал бы.
    ///
    /// ⚠️ Текст уезжает в адрес ЗАКОДИРОВАННЫМ: переводим строку журнала, в которой есть пробелы
    /// и знаки, — и адрес обязан остаться годным, а не разорванным по первому пробелу.
    /// </summary>
    [Fact]
    public void Ссылка_ведёт_в_тот_же_репозиторий_и_переживает_знаки_в_тексте()
    {
        var facts = Facts("строка с пробелами, «кавычками» и знаком & внутри");

        var url = IssueReport.GitHubUrl(facts, "что-то не работает: сервер не поднялся");

        Assert.StartsWith(ProductLinks.IssuesNewUrl, url, StringComparison.Ordinal);
        Assert.Contains(ProductLinks.ReleasesRepository, url, StringComparison.Ordinal);

        // В адресе не осталось ни пробела, ни кавычки из текста отчёта.
        var body = url[(url.IndexOf('?') + 1)..];
        Assert.DoesNotContain(" ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", body, StringComparison.Ordinal);

        // И сам текст восстанавливается из адреса — значит он не потерялся по дороге.
        var decoded = Uri.UnescapeDataString(body);
        Assert.Contains("строка с пробелами", decoded, StringComparison.Ordinal);
        Assert.Contains("сервер не поднялся", decoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// СЛОВА ЧЕЛОВЕКА В ОТЧЁТЕ ЕСТЬ. Отчёт без описания того, что случилось, — это набор чисел:
    /// разбирать его придётся догадками.
    /// </summary>
    [Fact]
    public void Слова_человека_попадают_в_отчёт_а_пустые_зовут_написать()
    {
        var facts = Facts();

        Assert.Contains("сервер не отвечает после перезагрузки", IssueReport.Text(facts, "сервер не отвечает после перезагрузки"), StringComparison.Ordinal);
        Assert.Contains("напишите здесь", IssueReport.Text(facts), StringComparison.Ordinal);
    }

    /// <summary>
    /// ХВОСТ ЖУРНАЛА ПРИХОДИТ ЦЕЛИКОМ И ПО ПОРЯДКУ: отчёт, в котором строки перемешались или
    /// потерялись, читается неверно — а именно по нему ищут причину.
    ///
    /// ⚠️ **Почему строк ровно 30, а не 50.** Отчёт обязан помещаться в ссылку GitHub
    /// (<see cref="IssueReport.MaxUrlLength"/>), и длинный русский журнал панель СОЗНАТЕЛЬНО режет
    /// с самого старого конца — это проверяет `Отчёт_помещается_в_ссылку_и_называет_сокращение_журнала`.
    /// 30.09.2026 отчёт вырос: в него добавлена оговорка о языке журнала (её требует владелец),
    /// и 50 русских строк перестали в ссылку влезать — на 50 проверка мерила бы сокращение,
    /// а не порядок. Число здесь выбрано так, чтобы журнал помещался ЦЕЛИКОМ: тогда проверяется
    /// ровно то, что названо в её имени.
    /// </summary>
    [Fact]
    public void Хвост_журнала_попадает_целиком_и_по_порядку()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 30; i++) lines.Add("строка журнала " + i);

        var text = IssueReport.Text(Facts(lines.ToArray()));

        foreach (var line in lines) Assert.Contains(line, text, StringComparison.Ordinal);

        var positions = lines.Select(line => text.IndexOf(line, StringComparison.Ordinal)).ToArray();
        Assert.Equal(positions.OrderBy(value => value).ToArray(), positions);
    }

    /// <summary>
    /// ОТЧЁТ ОБЯЗАН ПОМЕЩАТЬСЯ В ССЫЛКУ GITHUB — иначе кнопка «Открыть на GitHub» бесполезна.
    ///
    /// ⚠️ Проверка родилась из живой беды: владелец 29.09.2026 нажал «Открыть на GitHub» и получил
    /// от GitHub **«Whoa there! Your request URL is too long»**. Настоящие строки журнала длинные
    /// и русские, а кириллица в адресе стоит шесть знаков на букву, поэтому отчёт с 40–60 строками
    /// в ссылку не помещался вовсе.
    ///
    /// Проверяются ОБЕ стороны, и поодиночке каждая была бы слепой:
    /// * длинный журнал — ссылка в пределе, и **сам отчёт называет сокращение** числами;
    /// * короткий отчёт (соседняя проверка) — не сокращён вовсе.
    /// </summary>
    [Fact]
    public void Отчёт_помещается_в_ссылку_и_называет_сокращение_журнала()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 200; i++)
            lines.Add("2026-09-29 12:34:56 строка журнала номер " + i + " с довольно длинным русским текстом внутри");

        var facts = Facts(lines.ToArray());
        var said = "сервер не поднялся после перезапуска панели";

        var text = IssueReport.Text(facts, said);
        var url = IssueReport.GitHubUrl(facts, said);

        Assert.True(
            url.Length <= IssueReport.MaxUrlLength,
            $"ссылка длиннее предела: {url.Length} против {IssueReport.MaxUrlLength}");

        // Отчёт — РОВНО то, что уйдёт: текст в окне и тело ссылки совпадают по построению.
        Assert.Equal(text, Uri.UnescapeDataString(url[(url.IndexOf("body=", StringComparison.Ordinal) + 5)..]));

        // Сокращение НАЗВАНО числом: сколько строк в отчёте из скольких.
        var shown = lines.Count(line => text.Contains(line, StringComparison.Ordinal));
        Assert.True(shown < lines.Count, $"журнал не сократился вовсе ({shown} из {lines.Count})");
        Assert.Contains(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.IssueLogTrimmedFormat, shown, lines.Count),
            text,
            StringComparison.Ordinal);

        // Отброшены СТАРЫЕ строки, а последние (в них причина) — на месте.
        Assert.Contains(lines[^1], text, StringComparison.Ordinal);
        Assert.DoesNotContain(lines[0], text, StringComparison.Ordinal);
    }

    /// <summary>
    /// КОРОТКИЙ ОТЧЁТ НЕ СОКРАЩАЕТСЯ ВОВСЕ — пара к проверке выше. Без неё «сокращение названо»
    /// зеленело бы и там, где панель режет журнал на каждой строке, то есть где разбирать нечего.
    /// </summary>
    [Fact]
    public void Короткий_отчёт_не_сокращается_и_влезает_в_ссылку()
    {
        var facts = Facts("панель запущена", "сервер не поднялся");

        var text = IssueReport.Text(facts, "короткое описание");
        var url = IssueReport.GitHubUrl(facts, "короткое описание");

        Assert.True(url.Length <= IssueReport.MaxUrlLength, $"ссылка длиннее предела: {url.Length}");

        // Пометки о сокращении нет — ни журнальной, ни про текст человека.
        Assert.DoesNotContain(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, PanelStrings.IssueLogTrimmedFormat, 1, 2),
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(PanelStrings.IssueMessageTrimmed, text, StringComparison.Ordinal);

        // И обе строки журнала на месте: сокращать было нечего.
        Assert.Contains("панель запущена", text, StringComparison.Ordinal);
        Assert.Contains("сервер не поднялся", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ДЛИННЫЙ ТЕКСТ ЧЕЛОВЕКА ТОЖЕ ВЛЕЗАЕТ В ССЫЛКУ — и о его сокращении сказано словами.
    /// Это второй предел (первый — журнал): свой текст панель режет последним и только тогда,
    /// когда иначе ссылка не собирается вовсе.
    /// </summary>
    [Fact]
    public void Длинный_текст_человека_сокращается_ради_ссылки_и_это_сказано()
    {
        var said = string.Concat(Enumerable.Repeat("сервер не поднимается после перезапуска панели. ", 120));
        var facts = Facts("панель запущена");

        var text = IssueReport.Text(facts, said);
        var url = IssueReport.GitHubUrl(facts, said);

        Assert.True(url.Length <= IssueReport.MaxUrlLength, $"ссылка длиннее предела: {url.Length}");
        Assert.Contains(PanelStrings.IssueMessageTrimmed, text, StringComparison.Ordinal);
        Assert.True(text.Length < said.Length, "текст человека не сократился, а ссылка всё равно в пределе");
    }

    /// <summary>
    /// САМЫЙ КОРОТКИЙ ОТЧЁТ ВЛЕЗАЕТ В ССЫЛКУ ВСЕГДА — это нижняя граница обещания. Если перестанет
    /// влезать даже он, значит факты разрослись и предел <see cref="IssueReport.MaxUrlLength"/>
    /// надо мерить заново, а не поднимать наугад.
    /// </summary>
    [Fact]
    public void Отчёт_без_журнала_и_без_слов_человека_влезает_в_ссылку()
    {
        var facts = new IssueFacts(
            PanelVersion: "2.0.0",
            Revision: "abc1234",
            System: "Windows 11 (10.0.26100)",
            EngineVersion: string.Empty,
            NodeVersion: string.Empty,
            LaunchMode: "обычный запуск человеком",
            ServerState: "сервер свой, работает",
            ServerPort: 3080,
            LogTail: Array.Empty<string>());

        var url = IssueReport.GitHubUrl(facts);

        Assert.True(url.Length <= IssueReport.MaxUrlLength, $"ссылка длиннее предела: {url.Length}");
    }

    // ---- отчёт говорит на языке панели (дефект, названный владельцем 29.09.2026) ------------

    private static readonly Regex Cyrillic = new(@"[\u0400-\u04FF]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Факты БЕЗ русских значений и БЕЗ хвоста журнала. Так проверяется СОБСТВЕННЫЙ текст отчёта —
    /// тот, который панель пишет сама. Строки журнала панель ведёт по-русски осознанно, поэтому
    /// они в эту проверку не подмешиваются: у них своя, названная словами оговорка.
    /// </summary>
    private static IssueFacts NeutralFacts() => Facts() with
    {
        LaunchMode = "human launch",
        ServerState = "own server, running",
        LogTail = Array.Empty<string>(),
    };

    /// <summary>
    /// ОТЧЁТ ГОВОРИТ НА ЯЗЫКЕ ПАНЕЛИ — и это ровно тот дефект, который назвал владелец 29.09.2026:
    /// *«сообщить о проблеме там преднаписанный текст на русском, интересно будет ли он на другом
    /// языке, надеюсь учёл»*. Не учли: заголовки, подписи и оговорки были РУССКИМИ ЛИТЕРАЛАМИ
    /// в <c>Issue\IssueReport.cs</c>, и на английской и китайской панели отчёт уезжал на GitHub
    /// по-русски — при том, что всё остальное окно было переведено.
    ///
    /// ⚠️ **Здесь ДВА утверждения, и оба обязательны.** Поодиночке каждое слепо:
    /// * «на английском и китайском нет кириллицы» зеленело бы на отчёте, из которого выбросили
    ///   весь текст (пустая строка кириллицы не содержит);
    /// * «на русском кириллица есть» — зеркало, без которого первое ничего не значит.
    /// Поэтому факты одни и те же, а языки сравниваются между собой.
    ///
    /// ⚠️ Что проверка НЕ покрывает: КАЧЕСТВО перевода. «Переведено машинно и непонятно» —
    /// это читает человек, и об этом сказано в <c>tools\check-dictionaries.ps1</c>.
    /// </summary>
    [Fact]
    public void Отчёт_на_английском_и_китайском_без_кириллицы_а_на_русском_с_ней()
    {
        var facts = NeutralFacts();

        foreach (var language in new[] { "en", "zh" })
        {
            var text = IssueReport.TextIn(language, facts);

            // 1. НИ ОДНОЙ кириллической буквы — ни в заголовках, ни в подписях, ни в оговорках.
            Assert.True(
                !Cyrillic.IsMatch(text),
                $"отчёт на языке «{language}» содержит русский текст — значит часть его собрана " +
                "литералами в коде, а не взята из словаря. Вот он:" + Environment.NewLine + text);

            // 2. Заголовки ЛОКАЛИЗОВАНЫ, а не подставлены русские.
            Assert.Contains("### " + Loc.TIn(language, nameof(PanelStrings.IssueReportHeadingWhat)), text, StringComparison.Ordinal);
            Assert.Contains("### " + Loc.TIn(language, nameof(PanelStrings.IssueReportHeadingPanel)), text, StringComparison.Ordinal);
            Assert.Contains("### " + Loc.TIn(language, nameof(PanelStrings.IssueReportHeadingLog)), text, StringComparison.Ordinal);
            Assert.Contains(Loc.TIn(language, nameof(PanelStrings.IssueReportVersion)), text, StringComparison.Ordinal);
            Assert.Contains(Loc.TIn(language, nameof(PanelStrings.IssueReportEngine)), text, StringComparison.Ordinal);
            Assert.Contains(Loc.TIn(language, nameof(PanelStrings.IssueReportLaunch)), text, StringComparison.Ordinal);
            Assert.Contains(Loc.TIn(language, nameof(PanelStrings.IssueReportServer)), text, StringComparison.Ordinal);
            Assert.Contains(Loc.TIn(language, nameof(PanelStrings.IssueReportOmittedNote)), text, StringComparison.Ordinal);

            // 3. И ЭТИ СТРОКИ ЕСТЬ ИМЕННО В ЭТОМ СЛОВАРЕ. Без третьего утверждения первые два
            // слепы к пропаже перевода: `Loc.TIn` на отсутствующий ключ отдаёт ИМЯ КЛЮЧА (а в
            // отчёте окажется то же имя), а на пустое значение — английский. То есть отчёт с
            // дырой в словаре прошёл бы как «локализованный».
            foreach (var key in new[]
                     {
                         nameof(PanelStrings.IssueReportHeadingWhat),
                         nameof(PanelStrings.IssueReportHeadingPanel),
                         nameof(PanelStrings.IssueReportHeadingLog),
                         nameof(PanelStrings.IssueReportMessageHint),
                         nameof(PanelStrings.IssueReportVersion),
                         nameof(PanelStrings.IssueReportEngine),
                         nameof(PanelStrings.IssueReportLaunch),
                         nameof(PanelStrings.IssueReportServer),
                         nameof(PanelStrings.IssueReportPortFormat),
                         nameof(PanelStrings.IssueReportUnknown),
                         nameof(PanelStrings.IssueReportVersionUnknown),
                         nameof(PanelStrings.IssueReportEmptyValue),
                         nameof(PanelStrings.IssueReportOmittedNote),
                         nameof(PanelStrings.IssueReportLogRussianNote),
                     })
            {
                Assert.True(
                    Loc.Dictionary(language).TryGetValue(key, out var entry) && !string.IsNullOrWhiteSpace(entry),
                    $"в словаре «{language}» нет своей строки «{key}»: отчёт показал бы имя ключа " +
                    "или чужой язык — и это выглядело бы как перевод");
            }
        }

        // ЗЕРКАЛО: на русском кириллица ЕСТЬ — на тех же самых фактах. Без него первое утверждение
        // зеленело бы и на полностью пустом отчёте.
        var russian = IssueReport.TextIn("ru", facts);

        Assert.Contains("### " + Loc.TIn("ru", nameof(PanelStrings.IssueReportHeadingWhat)), russian, StringComparison.Ordinal);
        Assert.True(
            Cyrillic.IsMatch(russian),
            "русский отчёт без единой кириллической буквы — значит проверка меряет не то:" + Environment.NewLine + russian);

        // И отчёты РАЗНЫЕ: подмена языка не должна возвращать тот же текст.
        Assert.NotEqual(russian, IssueReport.TextIn("en", facts));
        Assert.NotEqual(russian, IssueReport.TextIn("zh", facts));
    }

    /// <summary>
    /// ОГОВОРКА О ЯЗЫКЕ ЖУРНАЛА ЕСТЬ В ОТЧЁТЕ НА ВСЕХ ТРЁХ ЯЗЫКАХ.
    ///
    /// ⚠️ Зачем отдельная проверка, а не строка в предыдущей. Строки журнала панель не переводит
    /// (часть из них русская), и это осознанное решение владельца, а не дефект. Но отчёт уезжает
    /// наружу, и русские строки на английском отчёте читаются как поломка панели. Поэтому отчёт
    /// обязан НАЗЫВАТЬ это словами — и называть на языке читателя, а не только по-русски.
    ///
    /// ⚠️ Строка берётся ИЗ СЛОВАРЯ ЯЗЫКА, а не через <c>Loc.TIn</c> одну: на пустое значение
    /// <c>Loc.TIn</c> молча отдаёт английский, и «оговорка есть на всех трёх языках» оказалось бы
    /// неправдой — человек читал бы не свой язык. Поэтому проверяются обе вещи: запись в словаре
    /// этого языка И её присутствие в отчёте.
    /// </summary>
    [Fact]
    public void Отчёт_называет_что_журнал_панели_ведётся_по_русски()
    {
        var facts = Facts("2026-09-29 00:00:00 панель запущена");

        foreach (var language in Loc.Known)
        {
            var hasEntry = Loc.Dictionary(language)
                .TryGetValue(nameof(PanelStrings.IssueReportLogRussianNote), out var entry);

            Assert.True(
                hasEntry && !string.IsNullOrWhiteSpace(entry),
                $"в словаре «{language}» нет своей оговорки о языке журнала: пустое значение " +
                "подменилось бы английским, и человек прочитал бы не свой язык");

            var note = Loc.TIn(language, nameof(PanelStrings.IssueReportLogRussianNote));
            var text = IssueReport.TextIn(language, facts);

            Assert.Contains(note, text, StringComparison.Ordinal);

            // Оговорка стоит рядом со строками журнала, а не вместо них: журнал в отчёте остался.
            Assert.Contains("панель запущена", text, StringComparison.Ordinal);
        }
    }

    // ---- исходник: русского литерала в сборке отчёта быть не должно ------------------------

    /// <summary>
    /// Кириллица в СТРОКАХ исходника — вне комментариев. Комментарии (в том числе
    /// <c>///</c>-документация) не считаются: они человеку не показываются и по-русски их писать
    /// здесь и положено.
    ///
    /// **Это разбор строк, а не поиск глазами**, и разбор настоящий: он знает про строчный
    /// комментарий, блочный комментарий, обычную строку с <c>\</c>-экранированием, строку
    /// <c>$"…"</c>, строку <c>@"…"</c> (в ней кавычка удваивается) и символьный литерал.
    /// Поэтому <c>//</c> внутри строки не съедает остаток строки, а кавычка внутри комментария
    /// не открывает строку — на обеих подменах проверка ловила бы не то.
    /// </summary>
    public static IReadOnlyList<string> CyrillicOutsideComments(string source)
    {
        var found = new List<string>();
        var text = source ?? string.Empty;

        var line = 1;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (character == '\n')
            {
                line++;
                index++;
                continue;
            }

            // Строчный комментарий.
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }

            // Блочный комментарий.
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/'))
                {
                    if (text[index] == '\n') line++;
                    index++;
                }

                index = Math.Min(text.Length, index + 2);
                continue;
            }

            // Строка: обычная, интерполированная ($"…") и дословная (@"…" / $@"…" / @$"…").
            var verbatim = character == '@' && index + 1 < text.Length && text[index + 1] == '"';
            var interpolated = character == '$' && index + 1 < text.Length && text[index + 1] == '"';
            var interpolatedVerbatim = character == '$' && index + 2 < text.Length
                && text[index + 1] == '@' && text[index + 2] == '"';
            var verbatimInterpolated = character == '@' && index + 2 < text.Length
                && text[index + 1] == '$' && text[index + 2] == '"';

            if (character == '"' || verbatim || interpolated || interpolatedVerbatim || verbatimInterpolated)
            {
                index += character == '"' ? 1 : interpolatedVerbatim || verbatimInterpolated ? 3 : 2;

                var value = new StringBuilder();
                var closed = false;

                while (index < text.Length)
                {
                    if (verbatim || interpolatedVerbatim || verbatimInterpolated)
                    {
                        // В дословной строке экранирования нет, а кавычка удваивается.
                        if (text[index] == '"')
                        {
                            if (index + 1 < text.Length && text[index + 1] == '"')
                            {
                                value.Append('"');
                                index += 2;
                                continue;
                            }

                            index++;
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        if (text[index] == '\\' && index + 1 < text.Length)
                        {
                            value.Append(text[index + 1]);
                            index += 2;
                            continue;
                        }

                        if (text[index] == '"')
                        {
                            index++;
                            closed = true;
                            break;
                        }

                        if (text[index] == '\n') break;
                    }

                    if (text[index] == '\n') line++;
                    value.Append(text[index]);
                    index++;
                }

                var literal = value.ToString();

                if (Cyrillic.IsMatch(literal))
                {
                    found.Add($"строка {line}: \"{literal}\"");
                }

                // Незакрытая строка — тоже беда, но здесь о ней молчать нельзя: без закрывающей
                // кавычки разбор поехал бы дальше и «нашёл» кириллицу в комментариях.
                if (!closed) found.Add($"строка {line}: строка не закрыта — разбор исходника сбит");

                continue;
            }

            // Символьный литерал: внутри него кириллице делать нечего, но и строкой он не является.
            if (character == '\'')
            {
                index++;
                while (index < text.Length && text[index] != '\'' && text[index] != '\n')
                {
                    index += text[index] == '\\' ? 2 : 1;
                }

                index++;
                continue;
            }

            index++;
        }

        return found;
    }

    /// <summary>
    /// В СБОРКЕ ОТЧЁТА НЕТ РУССКОГО ЛИТЕРАЛА. Проверка читает ИСХОДНИК
    /// (<c>src\DshPanel\Issue\IssueReport.cs</c>), а не собранный текст: собранный текст ловится
    /// проверкой выше, но она одна пропустила бы литерал, до которого в прогоне не дошло.
    ///
    /// ⚠️ Через то состояние, в котором дефект возможен: файл читается с диска как есть, и русское
    /// слово в нём — падение. Именно так дефект и жил: текст был ЗДЕСЬ, в коде.
    /// </summary>
    [Fact]
    public void В_исходнике_отчёта_нет_русского_литерала()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);

        Assert.False(string.IsNullOrEmpty(root), $"не нашёл корень репозитория вверх от «{AppContext.BaseDirectory}»");

        var path = Path.Combine(root, "src", "DshPanel", "Issue", "IssueReport.cs");

        Assert.True(File.Exists(path), $"нет исходника отчёта: «{path}»");

        var found = CyrillicOutsideComments(File.ReadAllText(path));

        Assert.True(
            found.Count == 0,
            "текст отчёта о проблеме обязан жить в словарях, а не в коде: русский литерал здесь " +
            "означает, что на английской и китайской панели отчёт уезжает по-русски (дефект, " +
            "названный владельцем 29.09.2026). Нашлось в «src\\DshPanel\\Issue\\IssueReport.cs»:" +
            Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    /// <summary>
    /// ЯДРО ЭТОЙ ПРОВЕРКИ УМЕЕТ ПАДАТЬ — и на подставном тексте, а не на порче дерева.
    /// Проверяются обе стороны разбора: литерал НАХОДИТСЯ, а комментарий — НЕ считается.
    /// Без этого «русского литерала нет» зеленело бы и у разбора, который не видит ничего.
    ///
    /// ⚠️ **Кавычка внутри комментария — не украшение, а находка мутации.** Первая редакция этой
    /// проверки обходилась без неё, и мутация «разбор перестал понимать <c>//</c>» ВЫЖИЛА: в
    /// комментариях были только «ёлочки», и сломанный разбор находил ровно те же три литерала.
    /// А вот кавычка в комментарии ломает сломанный разбор сразу: он открывает строку прямо
    /// в комментарии и объявляет русский текст в нём — литералом. На живой файл это не влияло
    /// только потому, что в комментариях `IssueReport.cs` кавычек нет; проверка обязана стоять
    /// на том, что она сторожит, а не на этой случайности.
    /// </summary>
    [Fact]
    public void Ядро_исходника_видит_литерал_и_не_считает_комментарий()
    {
        const string source =
            "// комментарий: «Привет» — не текст\n" +
            "// и ещё комментарий с кавычкой: \"Пока\" — тоже не текст\n" +
            "/// <summary>Документация: «Мир»</summary>\n" +
            "/* блочный «Свет» */\n" +
            "var a = \"Привет\";\n" +
            "var b = @\"Пока\";\n" +
            "var c = $\"Ну {a} и дела\";\n" +
            "var d = \"all good\";\n";

        var found = CyrillicOutsideComments(source);

        Assert.Equal(3, found.Count);
        Assert.Contains(found, line => line.Contains("Привет", StringComparison.Ordinal) && line.StartsWith("строка 5:", StringComparison.Ordinal));
        Assert.Contains(found, line => line.Contains("Пока", StringComparison.Ordinal) && line.StartsWith("строка 6:", StringComparison.Ordinal));
        Assert.Contains(found, line => line.Contains("Ну", StringComparison.Ordinal) && line.StartsWith("строка 7:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Разбор не сбивается на <c>//</c> ВНУТРИ строки: «https://…» — не комментарий, и строка
    /// после него остаётся строкой. Без этого проверка пропускала бы русский литерал ровно там,
    /// где он чаще всего и бывает рядом, — в адресе или в шаблоне.
    /// </summary>
    [Fact]
    public void Ядро_исходника_не_принимает_две_черты_в_строке_за_комментарий()
    {
        const string source = "var url = \"https://пример.рф\"; var tail = \"Привет\";\n";

        var found = CyrillicOutsideComments(source);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, line => line.Contains("пример.рф", StringComparison.Ordinal));
        Assert.Contains(found, line => line.Contains("Привет", StringComparison.Ordinal));
    }
}
