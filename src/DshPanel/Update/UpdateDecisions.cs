using System.Globalization;
using DshPanel.Isolation;
using DshPanel.Localization;
using DshPanel.Pricing;

namespace DshPanel.Update;

/// <summary>
/// РЕШЕНИЯ ОБ ОБНОВЛЕНИИ ПАНЕЛИ: какая версия новее, пора ли спрашивать выпуски, говорить ли
/// человеку шариком и какие заметки к выпуску ему показать.
///
/// Устроено как <see cref="PricingDecisions"/> и по той же причине: здесь нет ни сети, ни файлов,
/// ни таймера — только чистые функции от значений. Поэтому их можно перебрать проверками
/// (в том числе по всем сочетаниям аргументов), а «ходит ли панель в GitHub» решается отдельно
/// и по праву (<see cref="UpdateController"/>).
///
/// **Это только ПЕРВЫЙ срез — проверка и извещение.** Ни скачивания, ни SHA-256, ни замены
/// файлов, ни <c>update.cmd</c> здесь нет и быть не должно: их делает второй срез. Но ядро
/// нарочно разделено так, чтобы он лёг сверху: адрес выпуска и разбор ответа уже отделены
/// (<see cref="UpdateRelease"/>, <see cref="IUpdateClient"/>), а решения о версии и заметках
/// живут сами по себе и не изменятся, когда появится загрузка.
///
/// ⚠️ **Сравнение версий живёт ЗДЕСЬ и только здесь.** До 27.09.2026 числовая часть версии
/// вырезалась ещё и в окне «О программе» (<c>AboutWindow.NumericVersion</c>), то есть правил
/// было два. Второе правило однажды разошлось бы с первым — поэтому окно теперь зовёт
/// <see cref="Numeric"/> отсюда.
/// </summary>
public static class UpdateDecisions
{
    /// <summary>
    /// За какое время панель не спрашивает выпуски второй раз. Решение владельца: при запуске
    /// панели, но **не чаще раза в сутки** — то же правило, что у страницы цен.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Языки, блоки которых панель узнаёт в теле выпуска. Первый — язык-запасной: блок
    /// английского берётся, когда блока нужного языка нет или он пуст (правило панели 1.x).
    /// </summary>
    public const string FallbackLanguage = LanguageDecisions.English;

    /// <summary>
    /// ЧИСЛОВАЯ ЧАСТЬ ВЕРСИИ: <c>2.0.0+481fb6b</c> → <c>2.0.0</c>, <c>v1.21.0</c> → <c>1.21.0</c>.
    ///
    /// Хеш коммита к <c>ProductVersion</c> дописывает сам .NET, и человеку он ничего не говорит.
    /// Буква <c>v</c> спереди — обычай тегов GitHub: <c>v1.21.0</c> и <c>1.21.0</c> одна версия.
    ///
    /// ⚠️ **Единственная реализация в панели.** Её зовут и окно «О программе», и сравнение
    /// версий, и разбор ответа GitHub: второе такое правило однажды разошлось бы с первым.
    /// </summary>
    public static string Numeric(string? version)
    {
        var text = (version ?? string.Empty).Trim();

        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        return text.Trim();
    }

    /// <summary>
    /// НОВЕЕ ли предложенная версия, чем текущая.
    ///
    /// Правило проекта: **версию сравнивать по числовой части** (<c>-split '\+'</c>), потому что
    /// <c>+</c> — это хеш сборки, а не версия. Отсюда три шага:
    ///
    /// 1. числовая часть берётся у обеих версий (<see cref="Numeric"/>);
    /// 2. сравниваются ЧИСЛА, а не строки: «2.10.0» новее «2.9.9» (по строкам вышло бы
    ///    наоборот, и человек не получил бы новый выпуск никогда);
    /// 3. при равных числах решает предрелизный хвост: версия БЕЗ него — финальный выпуск,
    ///    и он новее предварительного («2.0.0» новее «2.0.0-rc.1»). Иначе человек, поставивший
    ///    предрелиз, не получил бы финальный выпуск никогда. Хвосты сравниваются по SemVer
    ///    («rc.10» новее «rc.2»).
    ///
    /// ⚠️ Хвост — решение, принятое здесь осознанно (в задаче названы «три числа»): панель 2.0
    /// выпускается предрелизами, и без этого правила предрелиз и финал считались бы одной
    /// версией. Числа остаются главными: без их равенства хвост не смотрится вовсе.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var left = Numeric(candidate);
        var right = Numeric(current);

        var leftNumbers = Numbers(left);
        var rightNumbers = Numbers(right);

        for (var index = 0; index < Math.Max(leftNumbers.Length, rightNumbers.Length); index++)
        {
            var a = index < leftNumbers.Length ? leftNumbers[index] : 0;
            var b = index < rightNumbers.Length ? rightNumbers[index] : 0;

            if (a != b) return a > b;
        }

        var leftPre = PreRelease(left);
        var rightPre = PreRelease(right);

        if (leftPre.Length == 0 && rightPre.Length == 0) return false;
        if (leftPre.Length == 0) return true;
        if (rightPre.Length == 0) return false;

        return ComparePreRelease(leftPre, rightPre) > 0;
    }

    /// <summary>
    /// ПОРА ЛИ СПРАШИВАТЬ ВЫПУСКИ. Решение владельца: при запуске панели, но НЕ чаще раза
    /// в сутки, и только там, где расписание вообще работает
    /// (<see cref="IsolationRules.ShouldRunScheduledWork"/>).
    ///
    /// Три случая, и каждый назван своей причиной:
    ///
    /// * отметки нет, она пуста или не разбирается — **пора**: панель, поставленная впервые,
    ///   обязана узнать о новом выпуске при первом же запуске, а не через сутки;
    /// * с отметки прошло меньше суток — **рано**;
    /// * **часы переведены назад** (отметка в будущем) — **рано, и это не ошибка**.
    ///   Иначе суточный гейт обходится переводом часов вперёд-назад: человек отматывает время,
    ///   панель считает «прошло больше суток» и стучится в GitHub сколько угодно раз.
    ///   Будущая отметка — такая же «рано», как вчерашняя: молчание здесь правильное поведение,
    ///   и говорить о нём человеку нечем (он не делал ничего плохого).
    ///
    /// ⚠️ Отметка — ТОГО ЖЕ формата, что у цен: круговая дата-время (<see cref="PricingDecisions.Stamp"/>).
    /// Формат и разбор берутся у цен, а не пишутся здесь заново: **вторая реализация формата
    /// однажды разошлась бы с первой**, и отметки перестали бы читаться.
    /// </summary>
    public static bool ShouldCheck(DateTimeOffset nowLocal, string? lastChecked, bool isolatedRun)
    {
        if (!IsolationRules.ShouldRunScheduledWork(isolatedRun)) return false;

        if (!PricingDecisions.TryParse(lastChecked, out var last)) return true;

        var elapsed = nowLocal - last;

        // Часы переведены назад: проверять НЕЛЬЗЯ (иначе суточный гейт обходится переводом часов),
        // но и ошибкой это не считается — отметка просто ещё «не наступила».
        if (elapsed < TimeSpan.Zero) return false;

        return elapsed >= CheckInterval;
    }

    /// <summary>
    /// ГОВОРИТЬ ЛИ ЧЕЛОВЕКУ ШАРИКОМ о новом выпуске.
    ///
    /// Три правила, и каждое выросло из случая:
    ///
    /// * **один раз на версию** — проверка идёт при каждом запуске панели, и без этого человек
    ///   получал бы один и тот же шарик каждый день, пока не обновится;
    /// * **никогда для пропущенной версии и всего, что её не новее** — человек сказал
    ///   «пропустить эту версию», и это его решение. Выпуск на GitHub мог и откатиться назад:
    ///   шарик про «2.1.0» после ответа про «2.2.0» только путает. А вот СЛЕДУЮЩИЙ выпуск новее
    ///   пропущенного — уже новость, и молчать о нём нельзя: иначе один пропуск выключил бы
    ///   извещения навсегда;
    /// * **старому — нет**: выпуск, который не новее установленной панели, не новость вовсе.
    ///
    /// Версия, о которой уже говорили, приходит в <paramref name="announced"/>: этого довольно,
    /// потому что отметка последней проверки и последняя увиденная версия — ОДНА запись панели.
    /// </summary>
    public static bool ShouldAnnounce(string? latest, string? skipped, string? current, string? announced)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (!IsNewer(latest, current)) return false;

        // Пропуск сравнивается как ВЕРСИЯ, а не как строка: «v2.1.0» и «2.1.0+хеш» — та же версия.
        if (!string.IsNullOrWhiteSpace(skipped) && !IsNewer(latest, skipped)) return false;

        if (string.IsNullOrWhiteSpace(announced)) return true;

        return IsNewer(latest, announced);
    }

    /// <summary>
    /// ЗАМЕТКИ К ВЫПУСКУ НА ЯЗЫКЕ ПАНЕЛИ.
    ///
    /// Тело выпуска разбито на блоки языка — **двумя способами сразу**, и оба обязаны работать:
    /// заголовками <c>## RU</c> / <c>## EN</c> / <c>## ZH</c> (регистр не важен) и машинными
    /// метками <c>&lt;!-- dsh-notes:ru --&gt;</c> … <c>&lt;!-- /dsh-notes:ru --&gt;</c> — так тело
    /// собирает выпускающий скрипт, и **так пришёл выпуск 1.21.0** (разбор границ —
    /// <see cref="NotesBlocks"/>). Граница обязана стоять ОДНА на всей строке: слова о границах
    /// внутри самих заметок иначе сбивали бы разбор. Ограждённые блоки (<c>```</c> и <c>~~~</c>)
    /// разбор не видит вовсе: тело — недоверенный текст, его правят руками на странице выпуска,
    /// а история, которая рассказывает про формат заметок и показывает пример границы
    /// в ограждении, объявила бы пример настоящим блоком.
    ///
    /// Три правила выбора, и все три взяты у панели 1.x (<c>dsh-tray\UpdateService.cs</c>,
    /// <c>PickNotes</c> и <c>NotesBlocks</c>) — там они проверены живыми выпусками:
    ///
    /// 1. блок нужного языка, если он есть и не пуст;
    /// 2. иначе — английский (<see cref="FallbackLanguage"/>): пустое окно человеку хуже,
    ///    чем заметки не на его языке;
    /// 3. английского нет — ПЕРВЫЙ НЕПУСТОЙ из остальных блоков в известном порядке
    ///    («ru», «zh», затем прочие по алфавиту): русский — источник истины проекта,
    ///    и он стоит первым не случайно. Отдать вместо него тело с заголовками разметки
    ///    значило бы показать человеку «## RU» и служебные строки как текст;
    /// 4. нет ни одного понятного блока — **весь текст как есть**: так выглядят выпуски
    ///    до появления языковых блоков, и показывать вместо них пустоту нельзя.
    ///
    /// ⚠️ Названием блока считается и человеческое имя языка («## Русский», «## English»,
    /// «## 中文»): переводы к выпускам ведёт человек, и требовать от него машинного кода
    /// значило бы однажды получить заметки на чужом языке из-за одной буквы.
    /// </summary>
    public static string PickNotes(string? raw, string? language)
    {
        var body = (raw ?? string.Empty).Trim();
        if (body.Length == 0) return string.Empty;

        var blocks = NotesBlocks(body);
        if (blocks.Count == 0) return body;

        var wanted = LanguageKey(language);
        if (wanted.Length > 0 && blocks.TryGetValue(wanted, out var own) && own.Length > 0) return own;

        foreach (var candidate in FallbackOrder(blocks))
        {
            if (blocks[candidate].Length > 0) return blocks[candidate];
        }

        // Блоки есть, а понятного текста в них нет: показываем тело как есть — ровно как панель 1.x.
        return body;
    }

    /// <summary>
    /// Порядок, в котором блоки берутся, когда своего языка нет: сперва английский, затем
    /// русский (источник истины проекта), затем китайский, затем прочие по алфавиту.
    /// Повторяемость здесь важнее вкуса: один и тот же выпуск обязан давать один и тот же текст.
    /// </summary>
    private static IEnumerable<string> FallbackOrder(Dictionary<string, string> blocks)
    {
        var ordered = new List<string>();

        foreach (var known in new[] { FallbackLanguage, "ru", "zh" })
        {
            if (blocks.ContainsKey(known)) ordered.Add(known);
        }

        ordered.AddRange(
            blocks.Keys
                .Where(key => !ordered.Contains(key, StringComparer.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

        return ordered;
    }

    /// <summary>
    /// Язык панели в виде имени блока: «ru», «en», «zh». Незнакомый (в том числе пустой) —
    /// пустая строка, то есть «своего блока нет»: выдумывать язык вместо названного нельзя.
    /// </summary>
    public static string LanguageKey(string? language)
    {
        var text = (language ?? string.Empty).Trim().ToLowerInvariant();

        return text switch
        {
            "ru" or "ru-ru" or "russian" => "ru",
            "en" or "en-us" or "en-gb" or "english" => "en",
            "zh" or "zh-cn" or "zh-hans" or "chinese" => "zh",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Числовая часть версии, разобранная на числа: «2.10.0-rc.1» → 2, 10, 0. Текст до хвоста
    /// читается по кускам до первой нецифры: «2.0.0» и «2.0» дают одно и то же начало, а
    /// «2.0.beta» — только два числа (дальше разбирать нечего).
    /// </summary>
    private static int[] Numbers(string version)
    {
        var head = version;
        var dash = head.IndexOf('-');
        if (dash >= 0) head = head[..dash];

        var parts = head.Split('.');
        var result = new List<int>();

        foreach (var part in parts)
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) break;

            result.Add(value);
        }

        return result.ToArray();
    }

    /// <summary>Предрелизный хвост версии: «2.0.0-rc.2» → «rc.2»; у финальной версии пусто.</summary>
    private static string PreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? string.Empty : version[(dash + 1)..].Trim();
    }

    /// <summary>
    /// Сравнение хвостов по правилам SemVer: числа сравниваются как числа («rc.10» новее
    /// «rc.2»), слова — по алфавиту, короткий список младше длинного. Числовой кусок младше
    /// буквенного — так требует SemVer, и это ровно тот случай, ради которого правило и нужно.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            if (index >= a.Length) return -1;
            if (index >= b.Length) return 1;

            var leftIsNumber = int.TryParse(a[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue);
            var rightIsNumber = int.TryParse(b[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightValue);

            if (leftIsNumber && rightIsNumber)
            {
                if (leftValue != rightValue) return leftValue.CompareTo(rightValue);
                continue;
            }

            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;

            var compare = string.Compare(a[index], b[index], StringComparison.OrdinalIgnoreCase);
            if (compare != 0) return compare;
        }

        return 0;
    }

    /// <summary>
    /// Разбор тела выпуска на блоки языка. Пусто — понятных блоков нет.
    ///
    /// ⚠️ **Панель узнаёт ДВА вида границ, и это не запас на всякий случай, а найденный дефект.**
    /// Заголовок второго уровня («## RU», «## Русский», «## English») — так тело выпуска выглядит
    /// на странице GitHub. Машинная метка в комментарии («&lt;!-- dsh-notes:ru --&gt;» …
    /// «&lt;!-- /dsh-notes:ru --&gt;») — так его собирает выпускающий скрипт, и **именно так
    /// пришёл выпуск 1.21.0**: панель 2.0 метки не понимала, блоков не находила и показывала
    /// тело ЦЕЛИКОМ, а первым в теле идёт английский — то есть на русской панели человек читал
    /// английские заметки (замечание владельца 28.09.2026). Правило панели 1.x — «метки знаем,
    /// заголовки тоже».
    ///
    /// ⚠️ **МЕТКИ СИЛЬНЕЕ ЗАГОЛОВКОВ, и это не тонкость, а второй найденный дефект.** Тело
    /// с метками несёт рядом человеческие заголовки («## English» НАД меткой и «## Русский»
    /// ПОД ней) — они для страницы выпуска, а не для разбора. Заголовок над меткой открывает
    /// блок с тем же ключом («en»), а повторная граница того же языка ничего не переписывает
    /// (правило «первый блок остаётся») — и текст метки уезжал в пустоту: словарь выходил
    /// <c>[en] = пусто, [ru] = текст</c>, панель на английском отдавала РУССКИЙ блок, потому что
    /// на пустой английский она смотрит как на отсутствующий. Поймано проверкой
    /// `Заметки_с_машинными_метками_берутся_на_языке_панели` (28.09.2026). Поэтому: **есть
    /// в теле хоть одна метка — границами считаются ТОЛЬКО метки**, а заголовки становятся
    /// обычным текстом внутри блока (ровно так их видит страница выпуска). Меток нет — работают
    /// заголовки, как прежде.
    ///
    /// Граница обязана стоять одна на всей строке; повторная граница того же языка ничего
    /// не переписывает (первый блок остаётся — так же ведёт себя панель 1.x). Незакрытое
    /// ограждение гасит разбор до своего закрытия: тело — недоверенный текст с GitHub, и пример
    /// метки внутри ограждённого блока границей не является.
    /// </summary>
    private static Dictionary<string, string> NotesBlocks(string body)
    {
        var marked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var language = string.Empty;
        var inMarker = false;
        var content = new List<string>();
        var fence = ' ';
        var fenceRun = 0;

        // Блок с метками кладётся в словарь ТОЛЬКО закрытым: незакрытая метка — это оборванный
        // перевод (маркер потерялся при правке тела на странице выпуска), и показывать его
        // половину хуже, чем не показывать вовсе. Блок под заголовком закрывать нечем — он
        // кладётся всегда.
        var closed = false;

        // Собирает открытый блок в СВОЙ словарь: блок с метками и блок с заголовком живут
        // раздельно ровно затем, чтобы к концу разбора выбрать один вид, а не мешать их в кучу.
        void Close()
        {
            if (language.Length == 0) return;

            // Незакрытый блок с метками отбрасывается ЦЕЛИКОМ — и здесь, и в конце разбора.
            if (inMarker && !closed)
            {
                language = string.Empty;
                inMarker = false;
                content.Clear();
                return;
            }

            var text = string.Join("\n", content).Trim();
            var into = inMarker ? marked : headed;

            if (!into.ContainsKey(language)) into[language] = text;

            language = string.Empty;
            inMarker = false;
            content.Clear();
        }

        foreach (var rawLine in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (fence != ' ')
            {
                // Внутри ограждения границей не считается ни одна строка — ни закрывающее
                // ограждение, ни строки между ними.
                if (IsFenceLine(rawLine, fence, fenceRun)) fence = ' ';
                if (language.Length > 0) content.Add(rawLine);
                if (fence == ' ') fenceRun = 0;
                continue;
            }

            if (IsFenceStart(rawLine, out var marker, out var run))
            {
                fence = marker;
                fenceRun = run;
                if (language.Length > 0) content.Add(rawLine);
                continue;
            }

            // Метка в комментарии — ДО заголовка: строка «<!-- dsh-notes:ru -->» заголовком
            // не является, и порядок проверок здесь решает.
            if (Marker(rawLine, out var name, out var closing))
            {
                if (!closing)
                {
                    Close();
                    language = name;
                    inMarker = true;
                    closed = false;
                    continue;
                }

                // Закрывающая метка закрывает ТОЛЬКО свой блок: чужая закрывающая — обычный
                // текст (комментарий, оставшийся в заметках, не должен обрывать перевод).
                if (inMarker && name == language)
                {
                    closed = true;
                    Close();
                }

                continue;
            }

            // Заголовок — граница ТОЛЬКО у тела без меток. В теле с метками он часть содержимого:
            // раз он не внутри открытого блока, он просто вступление и не сохраняется нигде.
            var heading = Heading(rawLine);
            if (heading is null)
            {
                if (language.Length > 0) content.Add(rawLine);
                continue;
            }

            if (inMarker) continue;

            Close();
            language = LanguageKey(heading);
        }

        Close();

        // Метки сильнее: тело, размеченное ими, разбирается ТОЛЬКО по ним.
        return marked.Count > 0 ? marked : headed;
    }

    /// <summary>
    /// Граница-метка в HTML-комментарии. <paramref name="closing"/> различает открывающую
    /// («&lt;!-- dsh-notes:ru --&gt;») и закрывающую («&lt;!-- /dsh-notes:ru --&gt;»): закрывающая
    /// без открывающей границей не считается — иначе комментарий, оставшийся в тексте заметок,
    /// обрывал бы настоящий перевод.
    ///
    /// Разбирается ровно то, что пишет выпускающий скрипт: комментарий ЦЕЛИКОМ на одной строке,
    /// внутри «dsh-notes:» и имя языка. Имя хранится как пришло (в словаре регистр не важен):
    /// панель не должна решать, какой язык «правильный», — выбор делает <see cref="LanguageKey"/>.
    /// </summary>
    private static bool Marker(string line, out string name, out bool closing)
    {
        name = string.Empty;
        closing = false;

        var text = line.Trim();
        if (!text.StartsWith("<!--", StringComparison.Ordinal)
            || !text.EndsWith("-->", StringComparison.Ordinal)
            || text.Length <= 7)
        {
            return false;
        }

        var inner = text[4..^3].Trim();

        if (inner.StartsWith("dsh-notes:", StringComparison.OrdinalIgnoreCase))
        {
            name = inner["dsh-notes:".Length..].Trim().ToLowerInvariant();
            return name.Length > 0;
        }

        if (inner.StartsWith("/dsh-notes:", StringComparison.OrdinalIgnoreCase))
        {
            name = inner["/dsh-notes:".Length..].Trim().ToLowerInvariant();
            closing = true;
            return name.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Заголовок второго уровня: строка из двух решёток, за которыми идёт имя языка, и ничего
    /// больше. Третий уровень и глубже не считается — панель 1.x разделяла заметки вторым,
    /// и «### RU» в теле выпуска это обычный подзаголовок внутри заметок.
    ///
    /// ⚠️ Знак ограждения (<c>`</c> или <c>~</c>) в имени тоже означает «не заголовок»: так
    /// выглядит пример формата заметок ВНУТРИ ограждённого блока, и признать его настоящим
    /// блоком значило бы отбросить настоящий перевод. Ограждение отслеживается отдельно,
    /// но подстраховка здесь не лишняя: незакрытое ограждение в теле выпуска — обычное дело.
    /// </summary>
    private static string? Heading(string line)
    {
        var text = line.TrimStart();
        if (!text.StartsWith("##", StringComparison.Ordinal)) return null;

        var rest = text[2..];

        // «###» и глубже — не наш заголовок.
        if (rest.StartsWith('#')) return null;

        var name = rest.Trim().TrimEnd('#').Trim();
        if (name.Length == 0) return null;

        // Имя языка — одно слово без знаков разметки: «## RU», «## English», «## 中文».
        return name.All(character =>
            !char.IsWhiteSpace(character) && character != '`' && character != '~')
            ? name
            : null;
    }

    /// <summary>
    /// Открывающее ограждение: три и больше знака <c>`</c> или <c>~</c> с отступом не больше трёх
    /// (отступ считается по-настоящему — табуляция за четыре пробела, — иначе «```» внутри
    /// списка с отступом открыло бы блок там, где Markdown блока не видит).
    /// <paramref name="run"/> — сколько знаков в ограждении: закрывающее обязано быть не короче.
    /// </summary>
    private static bool IsFenceStart(string line, out char marker, out int run)
    {
        marker = ' ';
        run = 0;

        if (FenceIndent(line, out var text) > 3) return false;
        if (text.Length < 3) return false;

        var symbol = text[0];
        if (symbol != '`' && symbol != '~') return false;

        var length = 0;
        while (length < text.Length && text[length] == symbol) length++;
        if (length < 3) return false;

        marker = symbol;
        run = length;
        return true;
    }

    /// <summary>
    /// Закрывающее ограждение: тот же знак, не короче открывшего, и больше ничего на строке
    /// (хвост после знаков допускается — Markdown его игнорирует).
    /// </summary>
    private static bool IsFenceLine(string line, char marker, int run)
    {
        if (FenceIndent(line, out var text) > 3) return false;
        if (text.Length < run || text[0] != marker) return false;

        var length = 0;
        while (length < text.Length && text[length] == marker) length++;
        return length >= run;
    }

    /// <summary>
    /// Начало строки: сколько в ней пробелов (табуляция — за четыре) и что идёт следом.
    /// Нужно ограждениям: Markdown допускает у начала блока не больше трёх пробелов, а
    /// с большим отступом ограждение — это уже код внутри списка, а не граница блока.
    /// </summary>
    private static int FenceIndent(string line, out string rest)
    {
        var spaces = 0;
        var index = 0;

        while (index < line.Length)
        {
            if (line[index] == ' ') spaces++;
            else if (line[index] == '\t') spaces += 4;
            else break;
            index++;
        }

        rest = line[index..].TrimEnd();
        return spaces;
    }
}
