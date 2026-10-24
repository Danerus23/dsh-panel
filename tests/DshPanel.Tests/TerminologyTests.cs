using System.Text.RegularExpressions;
using DshPanel.Localization;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ОДНО ПОНЯТИЕ — ОДНО ИМЯ (DEF-042, 2.4.1).
///
/// **От какого случая.** Замер 22.10.2026 (`_team\design-pass\p6-words.md` §8) нашёл в трёх
/// словарях по два-три имени на одну вещь: «настройки» и «Параметры» (21/1), «папка» и «каталог»
/// (55/31), по-китайски 「文件夹」 и 「目录」 (43/66), английские `Backup` / `Backups and restore` /
/// `Copies and restore`, китайское 「还原」 рядом с 「恢复」, и два РАЗНЫХ действия, названных почти
/// одинаково («Отвязаться» от сервера и «Отвязать» устройство телефона). Это не косметика: перевод
/// у панели машинный (`docs\LOCALIZATION.md` §6), и разные имена одного понятия он переводит как
/// РАЗНЫЕ понятия (Google «Write for a global audience»).
///
/// **Что здесь доказывается.** Перебором по ВСЕМ строкам всех трёх словарей — запрещённых и
/// двояких имён нет; у раздела копий одно имя; два отвязывания называются по-разному; словарь
/// терминов (`docs\LOCALIZATION.md` §5) не учит запрещённым словам и не содержит термина,
/// которого код не использует.
///
/// ⚠️ **Запрещённые слова перечислены ЗДЕСЬ, а не в §5.** Их место занимает банк
/// (`_design-library\70-writing.md` §3): слово, названное в словаре терминов один раз, читается
/// как разрешённое — ровно на этом §5 и спотыкался.
/// </summary>
public class TerminologyTests
{
    private static readonly string[] Languages = { "ru", "en", "zh" };

    /// <summary>
    /// Двоякие и запрещённые имена: понятие → слово, которого в словаре быть не должно, и
    /// НАЗВАННЫЕ послабления (технические имена Windows, частью которых это слово является).
    /// Числа «до» по каждому из них — в отчёте задачи и в `docs\LOCALIZATION.md` §5.
    /// </summary>
    private static readonly (string Language, string Word, string[] Allowed)[] Banned =
    {
        // настройки: было «настройки» 21 против «Параметры» 1
        ("ru", "Параметр", Array.Empty<string>()),

        // папка: было «папка» 55 против «каталог» 31 (по-китайски 文件夹 43 против 目录 66)
        ("ru", "каталог", Array.Empty<string>()),
        ("zh", "目录", new[] { "根目录", "目录链接" }),

        // копии: у раздела было три имени («Копии и восстановление» / «Резервное копирование»)
        ("ru", "Копии и восстановление", Array.Empty<string>()),

        // восстановление: китайское 「还原» рядом с 「恢复» (52 против 1)
        ("zh", "还原", Array.Empty<string>()),
        ("zh", "备份与恢复", Array.Empty<string>()),
        ("zh", "备份与还原", Array.Empty<string>()),

        // копии по-английски: `Backups and restore…` и `Copies and restore` при разделе `Backup`
        ("en", "Copies and restore", Array.Empty<string>()),
        ("en", "Backups and restore", Array.Empty<string>()),

        // слова, запрещённые банком (`70-writing.md` §3) и не используемые кодом.
        // «движк», а не «движок»: в словаре нашлось «ПО ПУТИ ДВИЖКА» — форма слова, которую
        // проверка по одной именительной форме пропустила бы.
        ("ru", "движк", Array.Empty<string>()),
        ("ru", "накат", Array.Empty<string>()),
        ("ru", "опись", Array.Empty<string>()),
        ("ru", "описи", Array.Empty<string>()),
        ("ru", "прогон проверки", Array.Empty<string>()),
        ("en", "engine", Array.Empty<string>()),
        ("en", "manifest", Array.Empty<string>()),
        ("en", "check run", Array.Empty<string>()),
        ("zh", "引擎", Array.Empty<string>()),
        ("zh", "清单", Array.Empty<string>()),
        ("zh", "自检运行", Array.Empty<string>()),

        // папка по-английски: `folder` и «directory» — одно имя (junction назван отдельно, §5)
        ("en", "directory", new[] { "directory link" }),
    };

    /// <summary>
    /// Запрещённые слова в САМОМ словаре терминов. Их быть не должно: §5 — источник терминов,
    /// и слово, названное в нём, дизайнер прочтёт как разрешённое.
    /// </summary>
    private static readonly string[] BannedInGlossary =
    {
        "движок", "накат", "опись", "прогон проверки", "Параметры", "каталог",
        "还原", "引擎", "清单", "自检运行", "manifest",
    };

    [Fact]
    public void Запрещённых_и_двояких_имён_нет_ни_в_одном_словаре()
    {
        // Словари читаются ТЕ ЖЕ, что и в окне: из сборки, а не из файлов исходников.
        foreach (var language in Languages) Assert.NotEmpty(Loc.Dictionary(language));

        foreach (var (language, word, allowed) in Banned)
        {
            var hits = new List<string>();

            foreach (var (key, value) in Loc.Dictionary(language))
            {
                var text = value;
                foreach (var exception in allowed)
                {
                    text = text.Replace(exception, string.Empty, StringComparison.OrdinalIgnoreCase);
                }

                if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) hits.Add($"{key}: «{value}»");
            }

            Assert.True(
                hits.Count == 0,
                $"{language}: двоякое/запрещённое «{word}» осталось в {hits.Count} строках — " +
                string.Join(" · ", hits.Take(5)));
        }
    }

    /// <summary>
    /// У РАЗДЕЛА КОПИЙ ОДНО ИМЯ: заголовок окна, кнопка-дверь и список функций называют раздел
    /// так же, как раздел настроек. У списка копий внутри окна — обычное слово для самой вещи,
    /// и оно тоже одно (третьего имени нет).
    /// </summary>
    [Fact]
    public void У_раздела_копий_одно_имя()
    {
        // Кнопка и заголовок окна добавляют к имени своё: многоточие и подпись программы.
        var tails = new[] { "…", " — DSH Panel", " — DSH 面板" };

        var headings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ru"] = "Копии",
            ["en"] = "Backups",
            ["zh"] = "备份",
        };

        foreach (var language in Languages)
        {
            var dictionary = Loc.Dictionary(language);
            var feature = Value(dictionary, "BackupSectionTitle");

            Assert.False(string.IsNullOrWhiteSpace(feature), $"{language}: у раздела копий нет имени");

            foreach (var key in new[] { "BackupsButton", "BackupWindowTitle", "PanelFunctionCopies" })
            {
                var shown = Value(dictionary, key);

                foreach (var tail in tails)
                {
                    if (shown.EndsWith(tail, StringComparison.Ordinal)) shown = shown[..^tail.Length];
                }

                Assert.Equal(feature, shown);
            }

            Assert.Equal(headings[language], Value(dictionary, "BackupHeading"));
        }
    }

    /// <summary>
    /// ДВА РАЗНЫХ ОТВЯЗЫВАНИЯ НАЗВАНЫ ПО-РАЗНОМУ. «Отвязаться» (сервер остаётся работать) и
    /// «Отвязать» (устройство уходит из списка) — разные действия с разными последствиями;
    /// одинаковые слова заставляли человека угадывать, какую связь он рвёт.
    /// </summary>
    [Fact]
    public void Два_разных_отвязывания_названы_по_разному()
    {
        foreach (var language in Languages)
        {
            var dictionary = Loc.Dictionary(language);

            var server = Value(dictionary, "DetachButton");
            var device = Value(dictionary, "PhoneForgetDeviceButton");

            Assert.False(string.IsNullOrWhiteSpace(server), $"{language}: у отвязывания сервера нет имени");
            Assert.False(string.IsNullOrWhiteSpace(device), $"{language}: у отвязывания устройства нет имени");

            Assert.NotEqual(server, device);

            // И одно не является началом другого: «Отвязать» / «Отвязать устройство» человек
            // всё равно прочтёт как одно и то же действие.
            Assert.False(
                device.StartsWith(server, StringComparison.Ordinal),
                $"{language}: «{device}» начинается с «{server}» — действия снова неразличимы");
        }
    }

    /// <summary>
    /// СЛОВАРЬ ТЕРМИНОВ СИНХРОНИЗИРОВАН С КОДОМ: каждое слово §5 живёт в словаре своего языка
    /// (иначе §5 учит слову, которого панель не говорит), а запрещённых слов в §5 нет вовсе.
    /// </summary>
    [Fact]
    public void Словарь_терминов_синхронизирован_с_кодом()
    {
        var root = SourceStringsTests.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.False(string.IsNullOrEmpty(root), "не нашёл корень репозитория");

        // В ПУБЛИЧНОМ ДЕРЕВЕ ВЫПУСКА ВНУТРЕННИХ ДОКУМЕНТОВ НЕТ (`docs/**` снимается фильтром выпуска).
        // Тогда проверяются словари (одно понятие — одно имя), а синхронизация с §5 — там, где §5 есть;
        // это названо, а не выдано за проверенное в публичном дереве.
        var documentPath = Path.Combine(root, "docs", "LOCALIZATION.md");
        if (!File.Exists(documentPath)) return;

        var document = File.ReadAllText(documentPath);
        var section = Section(document, "## 5. Словарь терминов");

        // Раздел не найден — сторож ослеп бы молча, поэтому это отказ, а не «нечего проверять».
        Assert.False(string.IsNullOrEmpty(section), "§5 словаря терминов не найден");

        foreach (var word in BannedInGlossary)
        {
            Assert.False(
                section.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"§5 называет запрещённое/двоякое «{word}» — это читается как разрешение");
        }

        var rows = GlossaryRows(section);

        // Перебор по пустому списку прошёл бы, ничего не проверив.
        Assert.True(rows.Count >= 20, $"§5: строк таблицы {rows.Count} — разбор сломался или таблицу вычеркнули");

        foreach (var (russian, english, chinese) in rows)
        {
            Assert.True(HasTerm("ru", russian), $"§5: термина «{russian}» нет в ru.json");
            Assert.True(HasTerm("en", english), $"§5: термина «{english}» нет в en.json");
            Assert.True(HasTerm("zh", chinese), $"§5: термина «{chinese}» нет в zh.json");
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary>Кусок документа от заголовка до следующего заголовка того же уровня.</summary>
    private static string Section(string document, string heading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var next = document.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);

        return next < 0 ? document[start..] : document[start..next];
    }

    /// <summary>Строки таблицы §5: три ячейки, шапка и разделитель пропускаются.</summary>
    private static List<(string Russian, string English, string Chinese)> GlossaryRows(string section)
    {
        var rows = new List<(string, string, string)>();

        foreach (var line in section.Split('\n'))
        {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|')) continue;
            if (trimmed.Contains("---", StringComparison.Ordinal)) continue;

            var cells = trimmed.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 3) continue;
            if (cells[0] is "русский" or "") continue;

            rows.Add((cells[0], cells[1], cells[2]));
        }

        return rows;
    }

    /// <summary>
    /// Есть ли термин в словаре языка. Русские слова в строках стоят в косвенных падежах
    /// («изолированном прогоне»), поэтому сверяется ОСНОВА первого слова: сравнение целой формы
    /// объявило бы живой термин пропавшим.
    /// </summary>
    private static bool HasTerm(string language, string cell)
    {
        var dictionary = Loc.Dictionary(language);

        var variants = Regex.Replace(cell, @"\([^)]*\)", string.Empty)
            .Split(new[] { '/', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(variant => variant.Trim())
            .Where(variant => variant.Length > 0);

        foreach (var variant in variants)
        {
            var word = variant.Split(' ')[0];
            var needle = language == "ru" && word.Length > 6 ? word[..6] : word;

            if (dictionary.Values.Any(value => value.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
