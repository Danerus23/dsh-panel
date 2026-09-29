using System.Globalization;

namespace DshPanel.Localization;

/// <summary>
/// Решения о языке интерфейса — ЧИСТЫЕ функции без состояния.
///
/// Почему отдельным файлом, а не внутри <see cref="Loc"/>. У <see cref="Loc"/> есть состояние
/// (выбранный язык, словари, список пропавших ключей), а состояние проверять неудобно: проверка
/// начнёт зависеть от порядка запуска, и «проверил три языка» превратится в «проверил тот язык,
/// который успел выставиться». Здесь же ничего не запоминается: пришло значение — вышел ответ.
/// Язык системы приходит АРГУМЕНТОМ: функция не спрашивает его сама, иначе её нельзя проверить
/// на трёх языках на одной машине.
///
/// Порядок выбора языка взят у панели v1 (<c>dsh-tray\Loc.cs</c>) дословно, он проверен жизнью:
/// переменная окружения сильнее настройки, «auto» и пустое значение означают язык системы,
/// а незнакомый язык системы — английский (лучше понятный не всем английский, чем пустое окно).
/// </summary>
public static class LanguageDecisions
{
    /// <summary>Значение «как в системе». Оно же — умолчание настройки и ответ на мусор в файле.</summary>
    public const string Auto = "auto";

    /// <summary>Язык-запаска: он есть всегда и на нём говорит весь исходный текст.</summary>
    public const string English = "en";

    /// <summary>Переменная окружения: перебивает настройку и ключ командной строки.</summary>
    public const string Variable = "DSH_PANEL_LANG";

    /// <summary>Ключ командной строки, которым снимают окна на трёх языках.</summary>
    public const string Switch = "--lang";

    /// <summary>
    /// Языки, на которых панель говорит. Первым идёт русский: он язык исходного текста,
    /// и порядок виден в окне настроек.
    /// </summary>
    public static readonly string[] Known = { "ru", "en", "zh" };

    /// <summary>
    /// Какой язык выбран. <paramref name="preference"/> — то, что пришло от настройки или ключом
    /// <c>--lang</c>, <paramref name="environmentValue"/> — значение <see cref="Variable"/>,
    /// <paramref name="systemLanguage"/> — язык системы (<c>TwoLetterISOLanguageName</c>).
    ///
    /// Переменная окружения сильнее настройки: так снимаются кадры окон на трёх языках,
    /// не трогая профиль человека.
    ///
    /// **Три входа — три разных ответа на незнакомое значение, и это осознанно:**
    ///
    /// * **переменная окружения** — английский. Её ставит человек в своей оболочке, отказывать ему
    ///   нечем, а пустое окно хуже понятного не всем языка. Эта ветка здесь и живёт;
    /// * **ключ <c>--lang</c>** — до этой функции не доходит вовсе: незнакомый язык отвергает
    ///   <see cref="RefuseSwitch"/> громким отказом кодом 2. Ключ — поверхность скриптов и съёмки,
    ///   и опечатка «--lang e» вместо «--lang en» молча положила бы в отчёт кадр не на том языке;
    /// * **настройка <c>language</c>** — тоже приходит сюда уже приведённой: файл настроек правят
    ///   руками, и мусор из него становится <c>auto</c> (<see cref="Normalize"/>, а до неё —
    ///   <c>SettingsStore.Clean</c>), то есть означает «как в системе», а не «английский».
    ///
    /// Незнакомое значение, пришедшее в <paramref name="preference"/> НАПРЯМУЮ, даёт английский —
    /// тот же ответ, что у мусора в переменной: вызывающий, добравшийся сюда в обход двух других
    /// дверей, получает понятный не всем язык, а не пустое окно. Так это и проверяется
    /// (<c>LanguageStateTests.Незнакомое_значение_доходит_до_решения_сырым</c>).
    /// </summary>
    public static string Resolve(string? preference, string? environmentValue, string? systemLanguage)
    {
        var forced = string.IsNullOrWhiteSpace(environmentValue) ? preference : environmentValue;

        var wanted = (forced ?? Auto).Trim().ToLowerInvariant();
        if (wanted.Length == 0 || wanted == Auto)
        {
            wanted = (systemLanguage ?? string.Empty).Trim().ToLowerInvariant();
        }

        return Array.IndexOf(Known, wanted) >= 0 ? wanted : English;
    }

    /// <summary>
    /// Значение настройки в сравнимом виде. Всё, чего панель не знает, — <c>auto</c>, то есть
    /// «как в системе»: файл настроек человек правит руками, и панель обязана это пережить,
    /// а не упасть и не показать пустое окно.
    /// </summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Array.IndexOf(Known, text) >= 0 ? text : Auto;
    }

    /// <summary>
    /// Почему с таким значением ключа <see cref="Switch"/> запускать НЕЛЬЗЯ. <c>null</c> — можно.
    ///
    /// Пустое значение и <c>auto</c> разрешены (это «как в системе»), известные языки — тем более
    /// (регистр не важен). Всё остальное — ОПЕЧАТКА, и она обязана быть громкой: ключ
    /// <c>--lang</c> — поверхность скриптов и съёмки, и «--lang e» вместо «--lang en» молча
    /// положило бы в отчёт кадр не на том языке. Ровно то же правило уже действует для опечатки
    /// в имени проверки и для <c>--shot</c> с незнакомым окном.
    ///
    /// Живёт здесь, а не в <c>Program.cs</c>, по той же причине, что и <see cref="Resolve"/>:
    /// текст отказа называет языки, а список языков — это <see cref="Known"/>, и второго места,
    /// где он переписан буквами, быть не должно.
    /// </summary>
    public static string? RefuseSwitch(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        if (text.Length == 0) return null;
        if (text.Equals(Auto, StringComparison.OrdinalIgnoreCase)) return null;
        if (Array.IndexOf(Known, text.ToLowerInvariant()) >= 0) return null;

        return $"ЯЗЫК ПРОВАЛ: ключ «{Switch}» не знает языка «{text}». " +
               $"Нужен один из: {string.Join(", ", Known)}.";
    }

    /// <summary>
    /// Что взять из словаря по ключу — и было ли чем взять.
    ///
    /// Правила взяты у v1 дословно: нет ключа в языке — берём английский; нет и там — возвращаем
    /// САМ КЛЮЧ и говорим об этом вызывающему (<paramref name="missing"/>). Пустое значение —
    /// это тоже пропуск, а не «перевод есть»: иначе пустой перевод молча подменялся бы английским
    /// и пропажа не была бы видна.
    ///
    /// <paramref name="missing"/> истинно ровно тогда, когда в языке ключа нет НАСТОЯЩЕГО,
    /// то есть он либо отсутствует, либо пуст.
    /// </summary>
    public static string Pick(
        string key,
        IReadOnlyDictionary<string, string>? language,
        IReadOnlyDictionary<string, string>? english,
        out bool missing)
    {
        if (language is not null && language.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
        {
            missing = false;
            return value;
        }

        missing = true;

        if (english is not null && english.TryGetValue(key, out var fallback) && !string.IsNullOrEmpty(fallback))
        {
            return fallback;
        }

        return key;
    }

    /// <summary>
    /// Номера подстановок в тексте: <c>{0}</c>, <c>{1:yyyy}</c> → 0 и 1. Экранированная скобка
    /// (<c>{{</c>) подстановкой не считается.
    ///
    /// Нужна для сверки трёх словарей: набор подстановок обязан совпадать во всех языках, иначе
    /// <c>string.Format</c> уронит строку в окне, и человек увидит не перевод, а исключение.
    /// </summary>
    public static List<int> Placeholders(string? text)
    {
        var found = new SortedSet<int>();
        var value = text ?? string.Empty;

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '{') continue;

            if (i + 1 < value.Length && value[i + 1] == '{')
            {
                i++;
                continue;
            }

            var j = i + 1;
            var number = 0;
            var digits = 0;

            while (j < value.Length && char.IsAsciiDigit(value[j]) && digits < 4)
            {
                number = (number * 10) + (value[j] - '0');
                digits++;
                j++;
            }

            if (digits == 0 || j >= value.Length) continue;

            if (value[j] == '}')
            {
                found.Add(number);
            }
            else if (value[j] == ':' && value.IndexOf('}', j) > j)
            {
                found.Add(number);
            }
        }

        return found.ToList();
    }

    /// <summary>Одинаковый ли набор подстановок у двух текстов. Формат рассказа — в <see cref="Placeholders"/>.</summary>
    public static bool SamePlaceholders(string? a, string? b)
    {
        var left = Placeholders(a);
        var right = Placeholders(b);

        return left.Count == right.Count && left.SequenceEqual(right);
    }

    /// <summary>
    /// Есть ли среди аргументов ключ <see cref="Switch"/> — даже без значения. Именно ЭТОТ ключ,
    /// а не «любой, что начинается с дефиса»: иначе «--shot» считался бы указанием языка.
    /// </summary>
    public static bool HasSwitch(IReadOnlyList<string>? args)
    {
        for (var i = 0; args is not null && i < args.Count; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Язык, названный ключом <c>--lang</c>. <c>null</c> — ключа нет либо он указан без языка
    /// (следом идёт другой ключ): тогда решает переменная окружения и настройка.
    /// </summary>
    public static string? SwitchValue(IReadOnlyList<string>? args)
    {
        for (var i = 0; args is not null && i < args.Count; i++)
        {
            if (!IsSwitch(args[i]) || !string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase)) continue;

            return i + 1 < args.Count && !IsSwitch(args[i + 1]) ? args[i + 1] : null;
        }

        return null;
    }

    /// <summary>
    /// Аргументы без пары «<c>--lang</c> <c>&lt;язык&gt;</c>»: ключ снимается ДО разбора режимов,
    /// как и <c>--run-root</c>, иначе разбор счёл бы его опечаткой.
    ///
    /// Значение снимается только у НЕ-ключа: иначе «--lang --shot кадр.png» съело бы съёмку,
    /// и панель открыла бы окно владельцу вместо кадра.
    /// </summary>
    public static string[] WithoutSwitch(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var rest = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            // Снимается РОВНО пара «--lang <язык>». Остальные ключи остаются на месте: иначе
            // зачистка съела бы сам режим, и «--lang zh --shot кадр.png» превратилось бы
            // в обычный запуск — то есть в окно на рабочем столе владельца.
            if (!string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
            {
                rest.Add(args[i]);
                continue;
            }

            if (i + 1 < args.Length && !IsSwitch(args[i + 1])) i++;
        }

        return rest.ToArray();
    }

    /// <summary>Язык системы в том виде, в каком его понимает <see cref="Resolve"/>.</summary>
    public static string SystemLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    private static bool IsSwitch(string argument) => argument.StartsWith('-');
}
