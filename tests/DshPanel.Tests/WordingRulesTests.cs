using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DshPanel.Localization;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// СЛОВА ЭКРАНА — ПРОВЕРКОЙ, А НЕ ПАМЯТЬЮ: жаргон, длина пояснения и класс ключа.
///
/// **От какого случая (полировка 13.10.2026).** Жаргон вычищался замером-скриптом
/// (`upd-build\writing-audit.ps1`), и это **не проверка**: скрипт живёт вне репозитория,
/// а словарь правит кто угодно. Вернуть «движок», «калитку» или «прогон проверки» можно было
/// так, что **ни один прогон не падал** — названная дыра приёмки. Здесь она закрыта: правило
/// становится проверкой, а проверка — падающей.
///
/// **Что проверяется:**
/// * **запрещённые слова** (`70-writing.md` §3) — ни в одной строке ДЛЯ ЧЕЛОВЕКА, во всех трёх
///   языках; строки журнала (`*Log`) исключены НАЗВАННЫМ отступлением №4;
/// * **капс внутри фразы** — русские слова из двух и более заглавных букв (аббревиатуры `DSH`,
///   `PID`, `SSH`, `КБ`… — разрешены списком);
/// * **класс ключа**: `*Hint` — короткая подсказка (без точки в конце, не длиннее `HintLimit`);
///   `*Note` — пояснение (не длиннее `NoteLimit`).
///
/// ⚠️ **Чего проверка НЕ видит и видеть не может:** понятности. «Фраза стала короче» и «фраза
/// стала понятной» — разные утверждения; смысл читает человек по кадру. И перевод: правило
/// «одно и то же слово в трёх языках» машиной не проверяется — оно проверяется совпадением
/// ключей (`LocalizationTests.Три_словаря_сходятся_ключ_в_ключ`).
/// </summary>
public class WordingRulesTests
{
    /// <summary>Сколько знаков позволено пояснению. 200 — мера задания: длиннее человек не читает.</summary>
    private const int NoteLimit = 200;

    /// <summary>Сколько знаков позволено короткой подсказке: одна фраза, а не абзац.</summary>
    private const int HintLimit = 120;

    /// <summary>Заглавные буквы, которые КАПСОМ и должны быть: аббревиатуры, а не крик.</summary>
    private static readonly HashSet<string> AllowedCaps = new(StringComparer.Ordinal)
    {
        "DSH", "PID", "SSH", "NTFS", "FAT", "KB", "MB", "GB", "КБ", "МБ", "ГБ", "ИД",
        "QR", "DHCP", "TCP", "MIT", "PATH", "APPDATA", "SSH", "HTTP", "HTTPS", "CPU", "GPU", "USB", "JSON", "ID",
    };

    /// <summary>
    /// ЗАПРЕЩЁННЫЕ СЛОВА — НИ В ОДНОЙ СТРОКЕ ДЛЯ ЧЕЛОВЕКА, В ТРЁХ ЯЗЫКАХ.
    ///
    /// Список — из `70-writing.md` §3 и решения владельца 13.10.2026 («движок» — жаргон).
    /// Проверка падает на ВОЗВРАТЕ слова; именно её отсутствие и было названной дырой.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть `движок` в `EngineNotesTitle`» ·
    /// «вернуть `калитку` в `PhoneNoDoor`» · «вернуть `опись` в `ZipManifestMissing`» ·
    /// «вернуть `PID` в `ConfirmStopFormat`» · «вернуть английское `engine`» · «вернуть китайское `引擎`».
    /// </summary>
    [Fact]
    public void Запрещённых_слов_в_строках_для_человека_нет()
    {
        // Слово → образцы, по которому оно узнаётся в своём языке.
        //
        // ⚠️ В КИТАЙСКОМ ЗАПРЕЩЁННЫХ СЛОВ ВЕЖЛИВОСТИ НЕТ, И ЭТО РЕШЕНИЕ, А НЕ ПРОПУСК: «请» —
        // грамматическая частица вежливой просьбы, а не заклинание; она стоит в 73 строках словаря,
        // включая наши же новые («请允许手机后重试»). Вежливость ловится в ru/en (пожалуйста/просто/
        // please/simply), обращение — проверкой `Обращение_к_человеку_единое` (только 您).
        var forbidden = new (string Word, string Language, string Pattern)[]
        {
            // ⚠️ ШАБЛОН «движ», А НЕ «движк» (F-2, пачка B). Прежний `движк` НЕ ловил НОМИНАТИВ
            // «движок» — самую вероятную форму возврата: проба 17.10.2026 давала движок = false,
            // а движка/движке/движку/движком/движки = true. Мутация, которую эта же проверка
            // называла обязательной («вернуть „движок“ в EngineNotesTitle»), не роняла ничего.
            ("движок", "ru", @"движ"),
            ("engine", "en", @"engine"),
            ("引擎", "zh", @"引擎"),
            ("калитка", "ru", @"калитк"),
            ("gate (о подключении телефона)", "en", @"\bgate\b"),
            ("闸门", "zh", @"闸门"),
            ("опись", "ru", @"опис(ь|и|ью)"),
            ("manifest (об описи архива)", "en", @"\bmanifest\b"),
            ("清单 (об описи архива)", "zh", @"清单"),
            ("замок", "ru", @"\bзамок|\bзамка|\bзамку|\bзамке|\bзамком"),
            ("lock (о наборе версий)", "en", @"\block\b"),
            ("прогон", "ru", @"прогон"),
            ("check run / test run", "en", @"\bcheck run\b|\btest run\b"),
            ("PID", "ru", @"\bPID\b"),
            ("PID", "en", @"\bPID\b"),
            ("PID", "zh", @"PID"),
            ("cookie", "ru", @"cookie"),
            ("cookie", "en", @"cookie"),
            ("подсказки (о файле версий)", "ru", @"подсказк"),

            // ВЕЖЛИВОСТЬ-ЗАКЛИНАНИЕ (S-08, S-15; GOV.UK error message, `70-writing.md` §3):
            // «please», «пожалуйста» и китайское «请» — те же заклинания, что «просто» и «извините»:
            // они не отвечают ни на один из двух вопросов человека и только удлиняют строку.
            ("пожалуйста / просто", "ru", @"пожалуйста|\bпросто\b"),
            ("please / simply", "en", @"\bplease\b|\bsimply\b"),
            // ⚠️ «请» в китайском НЕ заклинание, а грамматическая частица вежливой просьбы: она
            // стоит в 73 строках словаря и внутри НОВЫХ строк этой же пачки («请允许手机后重试»).
            // Поэтому в список запрещённых она не входит — это названное расхождение с §4.5 таблицы
            // `b-slova.md`, а не пропуск (мутация CE5 бьёт по английскому «please»).
        };

        var broken = new List<string>();

        foreach (var (word, language, pattern) in forbidden)
        {
            foreach (var (key, text) in HumanStrings(language))
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    broken.Add($"{language}/{key}: «{word}»");
                }
            }
        }

        Assert.True(
            broken.Count == 0,
            "в строках для человека вернулся жаргон:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// КАПС ВНУТРИ ФРАЗЫ — НОРМА §3 («sentence case для всего текста интерфейса»), И ПО ТРЁМ ЯЗЫКАМ.
    ///
    /// ⚠️ Русские слова берутся целиком: аббревиатуры (`DSH`, `PID`, `КБ`) отсеиваются списком,
    /// иначе проверка кричала бы на каждой строке с версией. Одиночная заглавная буква (начало
    /// предложения) тоже не считается — считается СЛОВО ИЗ ДВУХ И БОЛЕЕ ЗАГЛАВНЫХ.
    ///
    /// ⚠️ **ЛАТИНИЦА ТОЖЕ ПРОВЕРЯЕТСЯ (S-14, пачка B).** Прежде проверка смотрела только кириллицу
    /// (`[А-ЯЁ]{2,}`) и потому пропускала английский крик: замер по `en` дал **16** строк с капсом
    /// («NOT», «THEIR OWN», «OUTSIDE», «INCOMPLETE», «WARNING», «PRIVATE KEYS»…). Теперь крик
    /// запрещён в `en` так же, как в `ru`: важность несут порядок слов и отдельная строка (S-14).
    ///
    /// **Мутации, которые обязаны упасть здесь:** «вернуть `НЕ ПОЛНОСТЬЮ`» · «вернуть `ВНИМАНИЕ:`» ·
    /// «вернуть `NOT` в `UpdatePrepareNote`» (CE9).
    /// </summary>
    [Fact]
    public void Капса_внутри_фразы_нет()
    {
        var broken = new List<string>();

        foreach (var (key, text) in HumanStrings("ru"))
        {
            foreach (Match match in Regex.Matches(text, @"[А-ЯЁ]{2,}", RegexOptions.CultureInvariant))
            {
                if (AllowedCaps.Contains(match.Value)) continue;

                broken.Add($"ru/{key}: «{match.Value}»");
            }
        }

        foreach (var (key, text) in HumanStrings("en"))
        {
            // Спецификаторы формата (`{0:N0}`) — не слово: «N0» убираем ДО сверки, иначе проверка
            // кричала бы на каждой строке с числом (замер: 8 ложных срабатываний из 33).
            var prose = Regex.Replace(text, @"\{[^}]*\}", " ", RegexOptions.CultureInvariant);

            foreach (Match match in Regex.Matches(prose, @"\b[A-Z]{2,}\b", RegexOptions.CultureInvariant))
            {
                if (AllowedCaps.Contains(match.Value)) continue;

                broken.Add($"en/{key}: «{match.Value}»");
            }
        }

        Assert.True(
            broken.Count == 0,
            "в строках для человека вернулся капс:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// КЛАСС КЛЮЧА СООТВЕТСТВУЕТ ТЕКСТУ: `*Hint` — коротко и без точки, `*Note` — пояснение.
    ///
    /// **От какого случая (решение владельца 13.10.2026 №3).** Прежде `*Hint` означал И короткую
    /// подсказку, И длинное пояснение: замер давал 15 подсказок из одной фразы и 13 из нескольких,
    /// и механическая проверка «подсказка без точки» честной быть не могла — у пояснения точка
    /// уместна. Норма GOV.UK говорит прямо: «If you need to give a long, detailed explanation,
    /// do not use hint text» (`70-writing.md` §2.1).
    ///
    /// ⚠️ **Проверка смотрит только `*Hint` и `*Note`.** Остальные классы (`*Format`, `*Title`,
    /// `*Button`, `Tip*`, `*Log`) живут по своим правилам — их длина нормой не ограничена.
    ///
    /// Мутации, которые обязаны упасть здесь: «дописать точку в `BackupScopeExtraNote`» ·
    /// «перенести длинное пояснение обратно в `*Hint`».
    /// </summary>
    [Fact]
    public void Подсказка_короткая_а_пояснение_длинное()
    {
        var broken = new List<string>();

        foreach (var key in MemberNames())
        {
            var isHint = key.EndsWith("Hint", StringComparison.Ordinal);
            var isNote = key.EndsWith("Note", StringComparison.Ordinal);

            if (!isHint && !isNote) continue;

            // ⚠️ ПО ТРЁМ ЯЗЫКАМ (S-13, пачка B): предел считался только по `ru`, и потому четыре
            // английских `*Note` длиннее 200 жили спокойно. Языка нет вовсе — это пропуск перевода,
            // и о нём говорит `--lang-selftest`, а не эта проверка.
            foreach (var language in new[] { "ru", "en", "zh" })
            {
                var text = Loc.TIn(language, key);

                if (text.Length == 0 || string.Equals(text, key, StringComparison.Ordinal)) continue;

                if (isHint)
                {
                    if (text.EndsWith('.') || text.EndsWith('。'))
                        broken.Add($"{language}/{key}: подсказка кончается точкой — «{text}»");

                    if (text.Length > HintLimit)
                        broken.Add($"{language}/{key}: подсказка длиной {text.Length} знаков (предел {HintLimit}) — «{text}»");
                }
                else if (text.Length > NoteLimit)
                {
                    broken.Add($"{language}/{key}: пояснение длиной {text.Length} знаков (предел {NoteLimit}) — «{text}»");
                }
            }
        }

        Assert.True(broken.Count == 0, "класс ключа разошёлся с текстом:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// В СЛОВАХ КАРТОЧКИ ОБНОВЛЕНИЯ СЕРВЕРА DSH НЕТ НАШЕЙ МЕХАНИКИ.
    ///
    /// **От какого случая (решение владельца 14.10.2026).** На карточке стояло «рекомендуемая
    /// 0.1.5-rc.2 — **проверена пробой**», а в подсказке — «кнопка „Обновить“ ставит проверенную
    /// версию — ту, что **прошла пробу** панели». Владелец сказал прямо: **каким способом** панель
    /// проверила версию, человеку не нужно; это наша механика, а не его забота.
    ///
    /// ⚠️ **Список ключей — это И ЕСТЬ карточка**: те строки, которые она показывает человеку
    /// (заголовок, своя версия, решение, свежая, кнопки и подсказки к ним, выжимка про версии).
    /// Строки журнала (`*Log`) в список не входят: там внутренние имена разрешены отступлением №4.
    ///
    /// Мутации, которые обязаны упасть здесь: «вернуть „проверена пробой“ в
    /// `EngineUpdateRecommendedFormat`» · «вернуть „прошла пробу панели“ в `EngineUpdateMainHint`» ·
    /// «вернуть китайское „实测“ в `EngineUpdateFreshNoLockFormat`».
    /// </summary>
    [Fact]
    public void В_словах_карточки_обновления_нет_нашей_механики()
    {
        var keys = new[]
        {
            nameof(PanelStrings.EngineBlockTitle),
            nameof(PanelStrings.EngineOwnVersionFormat),
            nameof(PanelStrings.EngineOwnVersionUnknown),
            nameof(PanelStrings.EngineUpdateSameLine),
            nameof(PanelStrings.EngineUpdateNewerFormat),
            nameof(PanelStrings.EngineUpdateRecommendedFormat),
            nameof(PanelStrings.EngineUpdateFreshLineFormat),
            nameof(PanelStrings.EngineUpdateFreshNoLockFormat),
            nameof(PanelStrings.EngineUpdateMainHint),
            nameof(PanelStrings.EngineUpdateFreshHint),
            nameof(PanelStrings.EngineUpdateButton),
            nameof(PanelStrings.EngineUpdateFreshButton),
            nameof(PanelStrings.EngineReleasesLink),
            nameof(PanelStrings.EngineNotesTitle),
            nameof(PanelStrings.EngineNotesEmpty),
            nameof(PanelStrings.EngineNotesShowAll),
        };

        // Слово «проба/пробой» в трёх языках: у каждого перевода своё написание, и одно и то же
        // слово, оставленное в одном языке, ловится только своим образцом.
        var patterns = new[]
        {
            (Language: "ru", Word: "проба/пробой", Pattern: @"проб"),
            (Language: "en", Word: "probe", Pattern: @"\bprobe"),
            (Language: "zh", Word: "实测", Pattern: @"实测"),
        };

        var broken = new List<string>();

        foreach (var key in keys)
        {
            foreach (var (language, word, pattern) in patterns)
            {
                var text = Loc.TIn(language, key);

                // Языка нет вовсе — это пропуск перевода, и о нём говорит `--lang-selftest`.
                if (text == key || text.Length == 0) continue;

                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    broken.Add($"{language}/{key} («{word}»): {text}");
            }
        }

        Assert.True(
            broken.Count == 0,
            "в карточке обновления вернулась наша механика:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// СТРОКИ ДЛЯ ЧЕЛОВЕКА: ключи словаря без журнальных (`*Log`) — те живут по отступлению №4
    /// и внутренние имена в них разрешены.
    /// </summary>
    private static IEnumerable<(string Key, string Text)> HumanStrings(string language) =>
        Loc.Dictionary(language)
            .Where(pair => !pair.Key.Contains("Log", StringComparison.Ordinal))
            .Select(pair => (pair.Key, pair.Value ?? string.Empty));

    /// <summary>
    /// В РУССКОМ СЛОВАРЕ НЕТ СЛОМАННЫХ ФОРМ СЛОВА «ПРОВЕРКА» — СЛЕДА МЕХАНИЧЕСКОЙ ЗАМЕНЫ.
    ///
    /// **От какого случая (находка агента 14.10.2026, подтверждена проверяющим).** По `ru.json`
    /// прошла механическая замена слов — «прогон» → «проверка». Замена не знала про падежи, и в
    /// словаре остались формы, которых в русском языке НЕ БЫВАЕТ: «у этого **проверкаа** нет права»,
    /// «в изолированном **проверкае** не работают», «этому **проверкау** не разрешено». Строки
    /// журнальные (человек их не видит), поэтому до них не дотягивалась ни одна проверка: сторож
    /// словарей механический, `--lang-selftest` считает ключи и подстановки.
    ///
    /// ⚠️ **Что именно ловится:** «проверка» + ЕЩЁ ОДНА ГЛАСНАЯ (`а`, `у`, `е`, `и`, `о`, `ы`).
    /// Законные формы под правило не попадают: «проверка» (за ней пробел, запятая или конец строки),
    /// «проверке», «проверку», «проверки», «проверкой», «проверок» — у последних после «проверк»
    /// стоит не «а», а «е/у/и/о».
    ///
    /// ⚠️ **Это не отмена отступления №4** (`_design-library\90-our-deviations.md`): в ЖУРНАЛЕ
    /// внутренние имена — «прогон проверки», «движок» — разрешены. Запрещена **сломанная речь**.
    ///
    /// Мутация, которая обязана упасть здесь: **вернуть «проверкаа» в любую строку словаря**.
    /// </summary>
    [Fact]
    public void В_словарях_нет_сломанных_форм_слова_проверка()
    {
        var broken = new List<string>();

        foreach (var (key, text) in Loc.Dictionary("ru"))
        {
            foreach (Match match in Regex.Matches(
                         text ?? string.Empty, "проверка[ауеиоы]", RegexOptions.CultureInvariant))
            {
                broken.Add($"{key}: «{match.Value}» — «{text}»");
            }
        }

        Assert.True(
            broken.Count == 0,
            "в русском словаре сломанные формы слова «проверка» (след механической замены):" +
            Environment.NewLine + string.Join(Environment.NewLine, broken));

        // ВТОРАЯ ПОЛОВИНА ТОЙ ЖЕ БЕДЫ, одной строкой: замена «подсказки → список проверенных версий»
        // склеила «список проверенных версий О ВЕРСИЯХ сервера DSH». Проверяются ДВЕ строки по имени:
        // слова «версий о версиях» журнал может повторить и законно, а эти две — не должны.
        foreach (var key in new[]
                 {
                     nameof(PanelStrings.EngineHintsLoadedLog),
                     nameof(PanelStrings.EngineHintsUnavailableLog),
                 })
        {
            Assert.DoesNotContain("версий о версиях", Loc.TIn("ru", key), StringComparison.Ordinal);
        }
    }

    // ========================================================================
    // ЧЕТЫРЕ КЛАССА ПАЧКИ B (S-01…S-18). Все проверки смотрят ТРИ языка и строки ДЛЯ ЧЕЛОВЕКА
    // (`HumanStrings`), журнальные строки исключены отступлением №4 — но только от правил голоса:
    // сломанную речь журналу тоже запрещено (см. `В_словарях_нет_сломанных_форм_слова_проверка`).
    // ========================================================================

    /// <summary>
    /// КЛАСС 1 · СОГЛАСОВАНИЕ РОДА: определение и сказуемое обязаны согласоваться с нашим термином.
    ///
    /// **От какого случая (S-02, S-03, S-04).** По словарю прошла замена, и рядом с ЖЕНСКИМИ нашими
    /// терминами встали МУЖСКИЕ формы: «изолированный проверка», «этот проверка копии не снимает»,
    /// «список содержимого копии пуста / найдена / легла». Прежняя проверка следов замены знала один
    /// образец — `проверка[ауеиоы]` (склейка), — и все эти строки проходили зелёными.
    ///
    /// **Как ловится.** Таблица пар «форма-определение + наш термин»: род первого обязан совпасть
    /// с родом второго. Проверяются `ru` (в `en`/`zh` рода нет — там действуют свои правила).
    ///
    /// **Мутации, которые обязаны упасть здесь:** «вписать „изолированный проверка“ в
    /// `EnvLaunchIsolated`» (CE1) · «список содержимого копии пуста» в `ZipManifestEmpty` (CE4).
    /// </summary>
    [Fact]
    public void Согласование_рода_с_нашими_терминами()
    {
        // Мужские формы, которые НЕЛЬЗЯ ставить рядом с женским термином, и наоборот.
        var pairs = new (string Pattern, string Why)[]
        {
            (@"\b(изолированный|обычный|этот|наш|свой|каждый|один|самый)\s+(проверка|копия|запись|версия|папка|связь|настройка|страница|строка|ошибка|кнопка|справка)\b",
                "мужская форма рядом с женским термином"),
            (@"\bэтот\s+(проверка|копия|запись|версия|папка|связь|настройка|страница|строка|ошибка|кнопка)\b",
                "«этот» + женский термин"),
            (@"список содержимого копии\s+(пуста|найдена|легла|прочитана|собрана|пролегла|не\s+легла)\b",
                "сказуемое женского рода при «список»"),
            (@"\bсписка\s+содержимого\s+копии\s+нет\s+ни\s+одной\b",
                "потерянный предлог: «в списке … нет»"),
        };

        var broken = new List<string>();

        foreach (var (key, text) in Loc.Dictionary("ru"))
        {
            foreach (var (pattern, why) in pairs)
            {
                var match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

                if (match.Success) broken.Add($"ru/{key}: «{match.Value}» — {why}");
            }
        }

        Assert.True(
            broken.Count == 0,
            "в строках для человека рассыпалось согласование рода:" + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// КЛАСС 2 · СКЛЕЙКА ТЕРМИНА И ЕГО РЕГИСТР ВНУТРИ ФРАЗЫ.
    ///
    /// **От какого случая (S-05, S-10).** Замена «движок → Сервер DSH» оставила два следа:
    /// «DSH DSH connection failed» (`en`) и «Сервер DSH» с заглавной ВНУТРИ фразы в 22 строках
    /// для человека. Прежняя проверка капса искала `[А-ЯЁ]{2,}` — две кириллические заглавные
    /// подряд, — и одиночную «С» не видела вовсе; латиницу не видела тем более.
    ///
    /// **Как ловится.** (1) «Сервер DSH» с заглавной разрешён только в начале строки или после
    /// точки/восклицательного/вопросительного знака; (2) «DSH DSH» запрещено в любом языке.
    ///
    /// **Мутации, которые обязаны упасть здесь:** «вернуть „Сервер DSH“ в `EngineUpdateAskTitle`» ·
    /// «вернуть „DSH DSH“ в `PhoneEventBrokenLogFormat`» (журнал: склейка — не голос, а речь).
    /// </summary>
    [Fact]
    public void Термин_внутри_фразы_пишется_строчными()
    {
        var broken = new List<string>();

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var (key, text) in Loc.Dictionary(language))
            {
                if (Regex.IsMatch(text ?? string.Empty, @"DSH\s+DSH", RegexOptions.CultureInvariant))
                    broken.Add($"{language}/{key}: «DSH DSH» — склейка термина");
            }
        }

        // СКЛЕЙКА ПОРЯДКА СЛОВ (CE2): «DSH сервер» вместо «сервер DSH». Так выглядела строка
        // «связь с DSH сервера DSH отказал» — два DSH и перевёрнутый термин в одной фразе.
        // Проверяются ВСЕ строки (и журнальные): сломанная речь журналу тоже запрещена.
        foreach (var (key, text) in Loc.Dictionary("ru"))
        {
            var match = Regex.Match(text ?? string.Empty, @"\bDSH\s+сервер", RegexOptions.CultureInvariant);

            if (match.Success) broken.Add($"ru/{key}: «{match.Value}» — термин перевёрнут: «сервер DSH»");
        }

        foreach (var (key, text) in HumanStrings("ru"))
        {
            var value = text ?? string.Empty;

            foreach (Match match in Regex.Matches(value, "Сервер DSH", RegexOptions.CultureInvariant))
            {
                if (match.Index == 0) continue;                       // начало строки — заглавная законна

                var before = value[..match.Index];
                if (Regex.IsMatch(before, @"[.!?…]\s*$", RegexOptions.CultureInvariant)) continue; // после точки

                broken.Add($"ru/{key}: «…{value.Substring(Math.Max(0, match.Index - 12), Math.Min(12, match.Index))}Сервер DSH…» — заглавная внутри фразы");
            }
        }

        Assert.True(
            broken.Count == 0,
            "термин склеился или потерял регистр:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// КЛАСС 3 · ДВОЙНОЙ ТЕРМИН: у одного предмета одно имя (`70-writing.md` §4).
    ///
    /// **От какого случая (S-01, S-16).** В словаре жили ДВА имени одного предмета: «подключение
    /// **к** телефону» (панель ставит связь телефону) рядом с «подключение **с** телефона» (человек
    /// с телефона входит в панель) — и «наш сервер DSH» рядом с «сервер DSH панели». Плюс склейка
    /// «подключение к телефону **телефона**». Такой проверки не было ВООБЩЕ (дыра 7 реестра C1).
    ///
    /// **Как ловится.** Для каждого предмета задано РАЗРЕШЁННОЕ имя и список запрещённых синонимов:
    /// связь с телефоном — «с телефона» / 「手机连接」 (нельзя «к телефону», 「手机通道」);
    /// свой сервер — «сервер панели» (нельзя «наш сервер», «сервер DSH панели», «our DSH»,
    /// 「我们自己的DSH」).
    ///
    /// **Мутации, которые обязаны упасть здесь:** «вернуть „подключение к телефону“ в
    /// `PhoneRightNote`» (CE3) · «вернуть „наш сервер DSH“ в `FoundServerOwnMarkFormat`».
    /// ⚠️ **Расширено 15.10.2026 (BLOCK-2 реестра C1):** в `zh` третье имя того же предмета —
    /// не только 「我们自己的DSH」, но и 「我们自己的**服务器**」 — жило в
    /// `ServerStopWarningOwnFormat` и проходило зелёным, потому что шаблон знал только `DSH`.
    /// Шаблон расширен до `我们自己的\s*(DSH|服务器)`, значение приведено к 「面板服务器」.
    /// ⚠️ **Расширено 17.10.2026 (F-1 приёмки пачки B):** найденный сервер в `en` назывался ДВУМЯ
    /// именами — «found server» (9 клеток, `FoundServerForeignMark`) и «discovered server»
    /// (одна клетка, `SrvConsentForgottenOwnPortFormat`; вторая половина строки была дописана из
    /// прежнего текста, а не из таблицы). Проверка этого синонима не знала — теперь знает. Мутация
    /// `CF9` (вернуть `discovered server`) обязана падать здесь.
    /// </summary>
    /// </summary>
    [Fact]
    public void Одна_вещь_одно_имя()
    {
        var forbidden = new (string Language, string Pattern, string Why)[]
        {
            ("ru", @"к\s+телефону", "связь с телефоном — «с телефона», а не «к телефону»"),
            ("ru", @"подключени\w*\s+телефон\w*\s+телефон\w*", "термин склеен дважды"),
            ("ru", @"\bнаш\w*\s+сервер", "свой сервер — «сервер панели», а не «наш сервер»"),
            ("ru", @"сервер\s+DSH\s+панели", "два имени в одном: «сервер DSH» и «сервер панели»"),
            ("ru", @"\bсвой\w*\s+сервер\s+панели", "«свой» и «панели» — одно имя дважды"),
            ("en", @"\bour\s+(own\s+)?DSH\b", "свой сервер — «the panel's server», а не «our DSH»"),
            ("en", @"\bdiscovered\s+server\b", "найденный сервер — «found server», а не «discovered server»"),
            ("en", @"\bphone\s+(channel|gateway)\b", "связь с телефоном — «phone connection»"),
            ("zh", @"手机通道", "связь с телефоном — 「手机连接」"),
            ("zh", @"我们自己的\s*(DSH|服务器)", "свой сервер — 「面板服务器」"),
        };

        var broken = new List<string>();

        foreach (var (language, pattern, why) in forbidden)
        {
            foreach (var (key, text) in HumanStrings(language))
            {
                var match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

                if (match.Success) broken.Add($"{language}/{key}: «{match.Value}» — {why}");
            }
        }

        Assert.True(
            broken.Count == 0,
            "у одного предмета завелось второе имя:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// КЛАСС 4 · СЛЕД МАШИННОЙ ЗАМЕНЫ: НАША КУХНЯ В СЛОВАХ ДЛЯ ЧЕЛОВЕКА.
    ///
    /// **От какого случая (S-11, S-12).** Человеку показывали пути поиска и имя пакета
    /// («искали в PATH, %APPDATA%\npm и Program Files», «@deepseek-ai/dsh»), а также объясняли наши
    /// решения. Прежняя проверка знала один след — `проверка`+гласная; ни пути, ни имена пакетов
    /// она не видела.
    ///
    /// **Как ловится.** В строке для человека не может быть ни одного из: буквы диска с обратным
    /// слэшем, `%APPDATA%`, `%USERPROFILE%`, `Program Files`, `@deepseek-ai/`, имени файла состояния
    /// (`.yaml`, `.json`). Это же правило — красные линии 7 (личные пути) и база, п. 13.
    ///
    /// **Мутации, которые обязаны упасть здесь:** «вернуть `%APPDATA%\npm` в `SrvNoEngine`».
    /// </summary>
    [Fact]
    public void Нет_путей_и_имён_пакетов_в_строках_для_человека()
    {
        var patterns = new (string Pattern, string Why)[]
        {
            (@"[A-Za-z]:\\", "путь этой машины"),
            (@"%APPDATA%|%USERPROFILE%|%LOCALAPPDATA%", "переменная окружения"),
            (@"Program Files", "путь установки программы"),
            (@"@deepseek-ai/", "имя пакета"),
            (@"\bPATH\b", "имя переменной окружения"),
        };

        var broken = new List<string>();

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var (key, text) in HumanStrings(language))
            {
                foreach (var (pattern, why) in patterns)
                {
                    var match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.CultureInvariant);

                    if (match.Success) broken.Add($"{language}/{key}: «{match.Value}» — {why}");
                }
            }
        }

        Assert.True(
            broken.Count == 0,
            "в строках для человека наша кухня:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// S-18 · ОБРАЩЕНИЕ К ЧЕЛОВЕКУ ЕДИНОЕ, И В КИТАЙСКОМ ЭТО «ВЫ» (您).
    ///
    /// **От какого случая (S-18, решение капитана 17.10.2026).** Замер `zh`: **12** строк говорили
    /// человеку «ты» (你) против **20** — «вы» (您). В `ru` и `en` обращение ровное («вы» / «you»),
    /// а в китайском выбор ОБЯЗАТЕЛЕН: 你 — к близкому, 您 — вежливое. Панель говорит с человеком
    /// одинаково во всех 1293 строках, поэтому 你 в словаре быть не может.
    ///
    /// **Мутации, которые обязаны упасть здесь:** «вернуть 你 в `BackupPlanEmptyNoReason`» (CE10) ·
    /// «вернуть „ты“ в любую русскую строку».
    /// </summary>
    [Fact]
    public void Обращение_к_человеку_единое()
    {
        var broken = new List<string>();

        foreach (var (key, text) in HumanStrings("zh"))
        {
            if ((text ?? string.Empty).Contains('你')) broken.Add($"zh/{key}: «ты» (你) вместо «вы» (您) — {text}");
        }

        // Русское «ты» — тот же класс: панель обращается на «вы» (GOV.UK, Apple HIG Writing).
        var informal = new Regex(@"\b(ты|тебя|тебе|тобой|твой|твоя|твоё|твои)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        foreach (var (key, text) in HumanStrings("ru"))
        {
            var match = informal.Match(text ?? string.Empty);

            if (match.Success) broken.Add($"ru/{key}: «{match.Value}» — панель говорит «вы»");
        }

        Assert.True(
            broken.Count == 0,
            "обращение к человеку разошлось:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// S-17 · ПОДПИСЬ-МЕТКА НЕ КОНЧАЕТСЯ ДВОЕТОЧИЕМ — ПО ТРЁМ ЯЗЫКАМ.
    ///
    /// **От какого случая (S-17, реестр C1).** Подпись над списком бед стояла как «известные беды**:**».
    /// Норма GOV.UK (text input) и `70-writing.md` §2: подпись — существительное без двоеточия; важно
    /// то, что под ней стоит, а не знак. Случай исправлен в пачке B, но **проверки класса не было** —
    /// значит через месяц двоеточие вернулось бы так же тихо («починили случай, а не класс»).
    ///
    /// **Класс.** Подпись-метка — это строка для человека **без подстановки**, которая стоит над
    /// своим содержимым: `*Label` и подобные подписи (`EngineNotesTroubles`). Правило механическое:
    /// такая строка не заканчивается ни `:`, ни `：` (китайское двоеточие — тот же знак).
    ///
    /// **Исключения названы, а не сделаны молча:**
    /// * **подстановка `{N}`** — двоеточие законно: `EngineNotesForFormat` («про версию {0}:») —
    ///   это ПРЕФИКС строки, а не подпись, и текст после него дописывается;
    /// * **журнальные `*Log`** — их не видит человек, и двоеточие там разделитель
    ///   (`SettingsLoadLog` = «настройки:»); сторож слов и так смотрит только строки для человека.
    ///
    /// **Мутация, которая обязана упасть здесь:** вернуть двоеточие в подпись (`CE13`,
    /// `PhonePortLabel` = «Порт для телефона:»).
    /// </summary>
    [Fact]
    public void Подпись_не_кончается_двоеточием()
    {
        var broken = new List<string>();
        var labels = 0;

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var (key, text) in HumanStrings(language))
            {
                var value = (text ?? string.Empty).TrimEnd();
                if (value.Length == 0) continue;

                // Класс `*Label` — отдельным счётом: по нему видно, что правило не ослепло
                // на пустом наборе (иначе «нарушений нет» значило бы «смотреть не на что»).
                if (key.EndsWith("Label", StringComparison.Ordinal)) labels++;

                var colon = value.EndsWith(':') || value.EndsWith('：');
                if (!colon) continue;

                // ИСКЛЮЧЕНИЕ (названо выше): подстановка — значит это префикс строки, а не подпись.
                if (Regex.IsMatch(value, @"\{\d+\}", RegexOptions.CultureInvariant)) continue;

                broken.Add($"{language}/{key}: «{value}»");
            }
        }

        Assert.True(
            labels >= 20,
            $"подписей *Label в словарях {labels} — правило смотрит не на то (ждали не меньше 20)");

        Assert.True(
            broken.Count == 0,
            "подпись-метка кончается двоеточием (S-17):" + Environment.NewLine
            + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// КЛАСС 5 · СЛОВО НЕ СКЛЕЕНО И НЕ ОБРЕЗАНО — ЧЕТЫРЕ ПОВРЕЖДЕНИЯ ОДНОЙ МЕХАНИЧЕСКОЙ ПРАВКОЙ.
    ///
    /// **От какого случая (BLOCK-1 реестра C1, 27 клеток в трёх словарях).** Пачка B применяла
    /// таблицу слов подстановкой «новое вместо старого куска». Там, где кусок был ЦИТАТОЙ, а не
    /// значением, в словарь поехало повреждённое: куски новой строки **дописаны к старой**
    /// («Нажать нельзя: Нажать нельзя: …», 「正在正在停止…」), три строки **обрезаны до фрагмента**
    /// («What is not in here», «their own places»), в две попала **разметка таблицы** (`**`,
    /// «(без изменений)»), в восьми остался **осколок «…»** («the permissions… could not be
    /// restricted»). Набор из одиннадцати правил этого класса не видел ВООБЩЕ: 1960/1960 было
    /// зелёным, пока человек не открыл словарь.
    ///
    /// **Как ловится (механические правила, без списка ключей):**
    /// * **удвоение** — кусок от 10 знаков повторяется рядом сам с собой (разделитель — не больше
    ///   двух пробелов/запятых/「、」): `(\S.{8,78}\S)[ ,，、]{0,2}\1`;
    /// * **двойной знак** — `..`, `，，`, `。。`, `、、`, `：：` (след склейки «снимает.» + «.»);
    /// * **разметка** — `**` (маска токенов `***` вырезается заранее — она законна) и
    ///   «(без изменений)». ⚠️ **`|` намеренно не проверяется:** вертикальная черта законна
    ///   в журнальной строке `SrvEngineLineLogFormat` («сервер DSH| {0}»), и правило на `|`
    ///   падало бы на ней каждый прогон;
    /// * **обрезка** — строка-объяснение (`*Format`, `*Note`, `*Hint`, `*Warning`, `*Log`,
    ///   `*Stopped`, кроме `*Button`) не кончается многоточием, а у кнопок и пунктов меню
    ///   «…» — законное «откроется окно»;
    /// * **осколок** — многоточие СКЛЕЕНО со словом или знаком (`：…手机`, «the permissions… could»,
    ///   «手机连接已关闭…，»), либо строка начинается с «…». Законны: «…» в конце, «…» через пробел,
    ///   «……» по-китайски и «…» перед закрывающей кавычкой («“Пики и тарифы…”»).
    ///
    /// **Проверяются ВСЕ строки трёх словарей, включая журнальные.** Это не отмена отступления №4
    /// (в журнале разрешены наши имена) — журналу тоже запрещена СЛОМАННАЯ РЕЧЬ: пять из
    /// двадцати семи повреждённых клеток были именно журнальными.
    ///
    /// **Сторож проверяется собой:** каждый детектор обязан поймать свой образец повреждения
    /// в начале проверки — иначе он ослеп и «нарушений нет» ничего не стоит.
    ///
    /// **Мутации, которые обязаны упасть здесь:** `CE14` — удвоить хвост в `ru/TipBackupNotAllowed`
    /// (удвоение) · `CE15` — оставить осколок «…» в `en/PhoneCodeFailed` (осколок) ·
    /// «`**` в `ru/ZipManifestMissing`» (разметка) · «оборвать `en/SrvConsentForgottenOwnPortFormat`
    /// на «…»» (обрезка).
    /// </summary>
    [Fact]
    public void Слово_не_склеено_и_не_обрезано()
    {
        // СТОРОЖ ПРОВЕРЯЕТ СЕБЯ: каждый детектор обязан сработать на своём образце.
        Assert.True(
            Склейка("Нажать нельзя: Нажать нельзя: эта проверка копии не снимает."),
            "детектор удвоения ослеп — образец «Нажать нельзя: Нажать нельзя:» не поймался");
        Assert.True(
            Склейка("停止面板服务器、实时探测上一个版本并启动它——这需要几分钟、实时探测上一个版本并启动它——这需要几分钟"),
            "детектор удвоения не видит китайского повтора");
        Assert.True(ДвойнойЗнак("Нажать нельзя: эта проверка копии не снимает.."), "детектор двойного знака ослеп");
        Assert.True(Разметка("списка содержимого копии нет»** (без изменений)"), "детектор разметки ослеп");
        Assert.True(Осколок("面板服务器：…端口被其他程序占用"), "детектор осколка ослеп");
        Assert.True(Осколок("Could not show a code: …the phone connection is closed."), "детектор осколка не видит склейки в en");
        Assert.True(Осколок("…а без них список содержимого копии не собрать: {0}"), "детектор осколка не видит обрезки спереди");
        Assert.True(
            Обрезка(nameof(PanelStrings.SrvConsentForgottenOwnPortFormat), "port {0} now belongs to the panel…"),
            "детектор обрезки ослеп");
        Assert.False(
            Обрезка(nameof(PanelStrings.AboutButton), "О программе…"),
            "детектор обрезки считает порчей законное «откроется окно» у кнопки");
        Assert.False(Осколок("Пики и тарифы…». Так задумано"), "законное «…» перед кавычкой поймано как осколок");
        Assert.False(Осколок("正在启动……首次启动会构建配置"), "законное китайское «……» поймано как осколок");

        var broken = new List<string>();
        var scanned = 0;

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var (key, text) in Loc.Dictionary(language))
            {
                scanned++;
                var value = text ?? string.Empty;

                if (Склейка(value)) broken.Add($"{language}/{key}: удвоение — «{value}»");
                if (ДвойнойЗнак(value)) broken.Add($"{language}/{key}: двойной знак — «{value}»");
                if (Разметка(value)) broken.Add($"{language}/{key}: разметка таблицы — «{value}»");
                if (Осколок(value)) broken.Add($"{language}/{key}: осколок «…» — «{value}»");
                if (Обрезка(key, value)) broken.Add($"{language}/{key}: строка оборвана — «{value}»");
            }
        }

        // Сторож обязан смотреть на ВЕСЬ набор: 1294 ключа × 3 языка. Число — мера, а не обещание.
        Assert.True(
            scanned >= 3800,
            $"строк просканировано {scanned} — сторож смотрит не на весь словарь (ждали не меньше 3800)");

        Assert.True(
            broken.Count == 0,
            "в словаре повреждённые строки (склейка/обрезка/разметка/осколок):" + Environment.NewLine
            + string.Join(Environment.NewLine, broken));
    }

    /// <summary>Кусок от 10 знаков повторяется рядом сам с собой — след склейки «новое + старое».</summary>
    private static bool Склейка(string text) =>
        Regex.IsMatch(text, @"(\S.{8,78}\S)[ ,，、]{0,2}\1", RegexOptions.CultureInvariant);

    /// <summary>Двойной знак: «..», «，，», «。。», «、、», «：：».</summary>
    private static bool ДвойнойЗнак(string text) =>
        Regex.IsMatch(text, @"\.\.|，，|。。|、、|：：", RegexOptions.CultureInvariant);

    /// <summary>Разметка таблицы попала в строку: `**` (кроме маски `***`) и «(без изменений)».</summary>
    private static bool Разметка(string text) =>
        text.Replace("***", string.Empty, StringComparison.Ordinal).Contains("**", StringComparison.Ordinal)
        || text.Contains("(без изменений)", StringComparison.Ordinal);

    /// <summary>Осколок: многоточие склеено со словом или знаком, либо строка начинается с «…».</summary>
    private static bool Осколок(string text) =>
        text.StartsWith('…')
        || text.Contains("...", StringComparison.Ordinal)
        || Regex.IsMatch(text, @"(?<!…)…(?=[^\s""»”」…])|\s…\s", RegexOptions.CultureInvariant);

    /// <summary>Строка-объяснение оборвана многоточием (у кнопок и пунктов меню это законно).</summary>
    private static bool Обрезка(string key, string text)
    {
        var value = text.TrimEnd();

        if (!value.EndsWith('…') && !value.EndsWith("...", StringComparison.Ordinal)) return false;

        return Regex.IsMatch(key, "(Format|Note|Hint|Warning|Log|Stopped)$", RegexOptions.CultureInvariant)
               && !key.Contains("Button", StringComparison.Ordinal);
    }

    /// <summary>Имена членов <see cref="PanelStrings"/> — по ним видно КЛАСС ключа.</summary>
    private static IEnumerable<string> MemberNames() =>
        typeof(PanelStrings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => property.Name);
}
