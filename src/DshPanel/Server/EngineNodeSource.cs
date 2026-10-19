namespace DshPanel.Server;

/// <summary>
/// СВОЙ NODE: откуда панель его берёт и как проверяет.
///
/// **Зачем свой Node вообще.** Решение владельца 10.10.2026: проверяется **ВЕРСИЯ, а не наличие** —
/// `node --version` **≥ 20** (наш собственный измеренный порог: движок его не объявляет, объявляют
/// его зависимости). Системный годится; свой панель берёт **только когда его нет или он старше** —
/// и тогда он ставится в свою папку (<see cref="Isolation.AppPaths.NodeDir"/>), потому что
/// без Node движок не поставить: npm едет внутри Node.
///
/// **Источник — только официальный.** `nodejs.org/dist/&lt;версия&gt;/`, и это не «удобный адрес»,
/// а часть проверки: панель скачивает **официальный файл сумм** (`SHASUMS256.txt` того же выпуска),
/// берёт из него сумму НАШЕГО архива и требует, чтобы она совпала с закреплённой в сборке
/// (<see cref="ArchiveSum"/>). Два независимых источника обязаны сказать одно и то же; разошлись —
/// отказ, а не «поверим одному».
///
/// **Версия ЗАКРЕПЛЕНА, а не «последняя».** `latest-v22.x` — это движущаяся цель: завтра по тому же
/// адресу будет другой файл, и «проверено у одного, другое у другого» — ровно то, от чего замок
/// движка и заведён. Поэтому здесь стоит точная версия, а сумма — от неё.
///
/// ⚠️ **Чего здесь нет:** ни сети, ни диска. Это чистые факты и разбор строки файла сумм, поэтому
/// и проверяются они без сети (сама загрузка — <see cref="EngineNodeInstall"/>).
/// </summary>
public static class EngineNode
{
    /// <summary>
    /// Порог, ниже которого панель движок не ставит и берёт свой Node. **20** — измеренный порог
    /// (его объявляют зависимости движка; замер — `docs\ENGINE-INSTALL.md`).
    /// </summary>
    public const int MinimumMajor = 20;

    /// <summary>
    /// ВЕРСИЯ СВОЕГО NODE — точная (ветка LTS 22), а не «последняя»: по движущемуся адресу
    /// завтра окажется другой файл, и проверенная сумма перестанет что-либо значить.
    ///
    /// ⚠️ **Версия обязана годиться ЗАМКУ, и это проверяется** (`Самый_строгий_порог_замка`
    /// и `Свой_Node_годится_для_замка`): живой прогон в ВМ 10.10.2026 показал, что пакет из замка
    /// (`undici@8.11.2`) требует `node >= 22.19.0`, а стоявшая здесь `22.11.0` была старше —
    /// установка шла с извещениями `EBADENGINE`. Поэтому версия перезакреплена на `22.23.3`
    /// (официальный выпуск, сумма сверена), а проверка теперь ловит такое расхождение сама.
    /// </summary>
    public const string Release = "22.23.3";

    /// <summary>
    /// СУММА АРХИВА, ЗАКРЕПЛЁННАЯ В СБОРКЕ. Взята из официального `SHASUMS256.txt` выпуска
    /// (замер 10.10.2026) и **сверяется дважды**: с самим скачанным файлом и с тем, что говорит
    /// официальный файл сумм. Обновляется только вместе с <see cref="Release"/>.
    /// </summary>
    public const string ArchiveSum = "2b0ff57b049cda1bbcea2240eec20467018713c1efe1f7360c2681859b90ed71";

    /// <summary>Имя нашего архива в выпуске (Windows, 64 бита).</summary>
    public static string ArchiveName(string? release = null) => $"node-v{(release ?? Release).Trim()}-win-x64.zip";

    /// <summary>Адрес архива в официальном выпуске.</summary>
    public static string ArchiveUrl(string? release = null) => SumsRoot(release) + "/" + ArchiveName(release);

    /// <summary>Адрес официального файла сумм того же выпуска.</summary>
    public static string SumsUrl(string? release = null) => SumsRoot(release) + "/SHASUMS256.txt";

    /// <summary>Корень выпуска: <c>https://nodejs.org/dist/v&lt;версия&gt;</c>.</summary>
    public static string SumsRoot(string? release = null) => $"https://nodejs.org/dist/v{(release ?? Release).Trim()}";

    /// <summary>
    /// ПАПКА СВОЕГО NODE: <c>&lt;папка node&gt;\&lt;версия&gt;</c>. Версия — имя папки: две версии
    /// не смешиваются, а «который у нас стоит» видно по имени, без чтения файлов.
    /// </summary>
    public static string VersionDir(string? nodeDir, string? release = null)
    {
        if (string.IsNullOrWhiteSpace(nodeDir)) return string.Empty;

        return Path.Combine(nodeDir.Trim(), (release ?? Release).Trim());
    }

    /// <summary>Путь к <c>node.exe</c> в нашей папке этой версии.</summary>
    public static string ExePath(string? nodeDir, string? release = null)
    {
        var folder = VersionDir(nodeDir, release);
        return folder.Length == 0 ? string.Empty : Path.Combine(folder, "node.exe");
    }

    /// <summary>
    /// СТАРШАЯ ЧАСТЬ ВЕРСИИ: `v26.9.0` → 26; не разобралось → 0 («не узнать»). Разбор терпимый:
    /// версию печатают и `node --version`, и человек в настройках.
    /// </summary>
    public static int Major(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return 0;

        var text = versionText.Trim().TrimStart('v', 'V');
        var end = 0;

        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == 0) return 0;

        return int.TryParse(text[..end], out var major) ? major : 0;
    }

    /// <summary>
    /// Годится ли эта версия по НИЖНЕЙ ГРАНИЦЕ (20). Этого одного мало: настоящий порог объявляет
    /// ЗАМОК — см. <see cref="Fits"/>. Здесь остаётся только нижняя граница, когда замок молчит.
    /// </summary>
    public static bool Suits(string? versionText) => Major(versionText) >= MinimumMajor;

    /// <summary>
    /// ПОРОГ NODE ИЗ ДАННЫХ ЗАМКА, а не из головы (решение владельца 10.10.2026, коммит `7265d9d`):
    /// самый строгий `engines.node` среди записей замка; **20 остаётся нижней границей**, когда замок
    /// молчит (его нет, он пуст или ни одна запись порога не объявила).
    ///
    /// ⚠️ Живой прогон 10.10.2026: свой Node был `22.11.0`, а замок объявляет `>= 22.19.0`
    /// (`undici@8.11.2`, `@earendil-works/pi-ai`) — установка шла с извещениями `EBADENGINE`.
    /// Константа `20` этого не поймала бы НИКОГДА.
    /// </summary>
    public static string RequiredFrom(EngineTreeLock? engineLock)
    {
        var declared = StrictestFrom(engineLock);

        return declared.Length > 0 ? declared : MinimumMajor + ".0.0";
    }

    /// <summary>
    /// ГОДИТСЯ ЛИ NODE ДЛЯ ЭТОГО ЗАМКА — одной меркой и для СИСТЕМНОГО Node человека, и для своего,
    /// уже лежащего. Две разные мерки были бы дефектом: «системный берём по 20, а свой по замку»
    /// пустило бы в работу движок на Node, которого замок не допускает.
    /// </summary>
    public static bool Fits(string? versionText, EngineTreeLock? engineLock) =>
        Satisfies(versionText, RequiredFrom(engineLock));

    /// <summary>
    /// САМЫЙ СТРОГИЙ ПОРОГ NODE, КОТОРЫЙ ОБЪЯВЛЯЕТ САМ ЗАМОК. У 220 записей замка есть поле
    /// `engines.node` — от «*» до «>=22.19.0». Панель берёт из них **нижние границы** (`>=`, `>`,
    /// `^`, `~` и просто версию), отбрасывает «*» и составные условия с `||` и оставляет самую
    /// высокую: свой Node обязан быть не старше неё.
    ///
    /// ⚠️ **Зачем это, а не «поставим LTS поновее».** Живой прогон в ВМ 10.10.2026: свой Node был
    /// `22.11.0`, а `undici@8.11.2` из ЭТОГО ЖЕ замка требует `>= 22.19.0` — установка шла с
    /// извещениями `EBADENGINE`, и это могло кончиться бедой в работе. Порог берётся из данных,
    /// а не из головы: обновят замок — проверка скажет.
    /// </summary>
    public static string StrictestFrom(EngineTreeLock? engineLock)
    {
        if (engineLock is null || !engineLock.Any) return string.Empty;

        var strictest = string.Empty;

        foreach (var entry in engineLock.Entries)
        {
            var lower = LowerBound(entry.EnginesNode);
            if (lower.Length == 0) continue;

            if (strictest.Length == 0 || Compare(lower, strictest) > 0) strictest = lower;
        }

        return strictest;
    }

    /// <summary>
    /// САМЫЙ СТРОГИЙ ПОРОГ СРЕДИ ВСЕХ ЗАМКОВ СБОРКИ (задача `t4`, блок B: замков теперь два).
    ///
    /// **Зачем.** Один Node на машине обслуживает ОБЕ версии движка, которые панель держит на диске.
    /// Если порог брать только у той версии, которую ставим сейчас, откат мог бы поднять прежнюю
    /// версию на Node, которого её замок не допускает, — то есть панель своими руками сделала бы
    /// ровно то, от чего замок и заведён. Поэтому требование — **не слабее самого строгого**.
    /// </summary>
    public static string StrictestFrom(EngineTreeLocks? locks)
    {
        if (locks is null || !locks.Any) return string.Empty;

        var strictest = string.Empty;

        foreach (var engineLock in locks.All)
        {
            var lower = StrictestFrom(engineLock);
            if (lower.Length == 0) continue;

            if (strictest.Length == 0 || Compare(lower, strictest) > 0) strictest = lower;
        }

        return strictest;
    }

    /// <summary>
    /// ЗАМОК, КОТОРЫЙ ТРЕБУЕТ САМЫЙ СВЕЖИЙ NODE, — его и зовут двери, добывающие Node: `Ensure`
    /// принимает один замок, а требование должно быть общим (см. <see cref="StrictestFrom(EngineTreeLocks)"/>).
    /// Замков нет — пустой замок, и порог падает к нижней границе.
    /// </summary>
    public static EngineTreeLock StrictestLock(EngineTreeLocks? locks)
    {
        if (locks is null || !locks.Any) return EngineTreeLock.Empty;

        var best = locks.Primary.Any ? locks.Primary : locks.All[0];
        var bestNeed = StrictestFrom(best);

        foreach (var engineLock in locks.All)
        {
            var need = StrictestFrom(engineLock);
            if (need.Length == 0) continue;

            if (bestNeed.Length == 0 || Compare(need, bestNeed) > 0)
            {
                best = engineLock;
                bestNeed = need;
            }
        }

        return best;
    }

    /// <summary>Не старше ли эта версия объявленного порога. Порог пуст — «требовать нечего».</summary>
    public static bool Satisfies(string? version, string? minimum)
    {
        var need = Normalize(minimum);
        if (need.Length == 0) return true;

        var have = Normalize(version);
        if (have.Length == 0) return false;

        return Compare(have, need) >= 0;
    }

    /// <summary>
    /// НИЖНЯЯ ГРАНИЦА из условия: `>=22.19.0` · `>22` · `^22.11.0` · `~22.11.0` · `22.11.0` → `22.19.0`
    /// (или `22.0.0`, `22.11.0`). «*», пустое и условия с `||` нижней границы не дают: панель
    /// не выдумывает требование там, где его не объявили.
    /// </summary>
    public static string LowerBound(string? engines)
    {
        if (string.IsNullOrWhiteSpace(engines)) return string.Empty;

        var text = engines.Trim();
        if (text == "*" || text.Contains("||", StringComparison.Ordinal)) return string.Empty;

        var strictest = string.Empty;

        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            var op = string.Empty;

            foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
            {
                if (!part.StartsWith(candidate, StringComparison.Ordinal)) continue;
                op = candidate;
                part = part[candidate.Length..].Trim();
                break;
            }

            // Верхние границы (`<`, `<=`) и «примерно равно» вниз не тянут — берём только нижние.
            if (op is "<" or "<=") continue;

            var version = Normalize(part);
            if (version.Length == 0) continue;

            if (strictest.Length == 0 || Compare(version, strictest) > 0) strictest = version;
        }

        return strictest;
    }

    /// <summary>Версия в вид `X.Y.Z`: `22` → `22.0.0`, `v22.19` → `22.19.0`, мусор → пусто.</summary>
    public static string Normalize(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return string.Empty;

        var text = versionText.Trim().TrimStart('v', 'V');
        var parts = new List<int>();

        foreach (var raw in text.Split('.'))
        {
            var digits = new string(raw.TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0) break;

            if (!int.TryParse(digits, out var number)) break;

            parts.Add(number);
            if (parts.Count == 3) break;
        }

        if (parts.Count == 0) return string.Empty;

        while (parts.Count < 3) parts.Add(0);

        return string.Join('.', parts);
    }

    /// <summary>Сравнение версий по частям: `22.23.3` против `22.19.0`.</summary>
    public static int Compare(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);

        if (a.Length == 0 || b.Length == 0) return 0;

        var one = a.Split('.').Select(int.Parse).ToArray();
        var two = b.Split('.').Select(int.Parse).ToArray();

        for (var index = 0; index < 3; index++)
        {
            if (one[index] != two[index]) return one[index] < two[index] ? -1 : 1;
        }

        return 0;
    }

    /// <summary>
    /// СУММА НАШЕГО АРХИВА ИЗ ОФИЦИАЛЬНОГО ФАЙЛА СУММ. Формат строки: `&lt;64 знака&gt;  &lt;имя файла&gt;`
    /// (разделитель — два пробела, но терпим и к одному: файл чужой, и падать из-за пробела нельзя).
    /// Имя файла ищется ТОЧНО — «похожий» архив не подходит.
    ///
    /// Имени нет или сумма не той длины — **пустая строка**: это «нечем сверять», а не «сошлось».
    /// </summary>
    public static string SumFrom(string? sumsText, string? archiveName)
    {
        if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(archiveName)) return string.Empty;

        var wanted = archiveName.Trim();

        foreach (var raw in sumsText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var split = line.IndexOfAny(new[] { ' ', '\t' });
            if (split <= 0) continue;

            var sum = line[..split].Trim();
            var name = line[split..].Trim().TrimStart('*');

            if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            return sum.Length == 64 && sum.All(Uri.IsHexDigit) ? sum.ToLowerInvariant() : string.Empty;
        }

        return string.Empty;
    }
}
