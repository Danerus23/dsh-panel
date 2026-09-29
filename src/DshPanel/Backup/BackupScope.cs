namespace DshPanel.Backup;

/// <summary>
/// РЕЖИМ ОБЪЁМА КОПИИ — то, сколько данных человек кладёт в архив. Формулировка владельца
/// записана в <c>PROJECT.md</c> §3.3 и здесь не выдумывается заново:
///
/// * <see cref="Auto"/> — «всё нужное без восстановимого мусора, <c>.git</c> остаётся»: ровно то,
///   что панель делала до v2.2 одним-единственным способом;
/// * <see cref="Full"/> — «всё как есть, ничего не пропускать»: в копию едет и то, что
///   восстанавливается сборкой (<c>node_modules</c>, <c>bin</c>, <c>obj</c>, <c>logs</c>);
/// * <see cref="Custom"/> — автоматический плюс ЛИШНИЕ имена папок, названные человеком
///   (<c>vendor</c>, <c>target</c>, <c>.venv</c>): список живёт в настройках и действует на все корни.
///
/// Режим — решение ЧЕЛОВЕКА, а не догадка панели: поэтому здесь нет умолчания «по размеру диска»
/// или «по типу проекта». Панель показывает три варианта, объясняет каждый словами и делает тот,
/// который выбран.
/// </summary>
public enum BackupScope
{
    /// <summary>Автоматический: всё нужное без восстановимого мусора, <c>.git</c> остаётся.</summary>
    Auto,

    /// <summary>Полный: всё как есть, ничего не пропускать (в планировщике — <c>everything: true</c>).</summary>
    Full,

    /// <summary>Свой фильтр: автоматический плюс названные человеком лишние имена папок.</summary>
    Custom,
}

/// <summary>
/// РЕШЕНИЯ О РЕЖИМЕ ОБЪЁМА — чистые функции: ни диска, ни настроек, ни движка. Всё, что нужно,
/// приходит параметром, поэтому правила перебираются проверками, а не «выглядят верными».
///
/// ⚠️ **Имена режимов в файле настроек — ЛАТИНИЦЕЙ** (<c>auto</c>, <c>full</c>, <c>custom</c>),
/// как у темы и языка. Причина та же: файл человек правит руками, и значение в нём обязано быть
/// читаемым без словаря, а панель обязана пережить мусор — неизвестное значение становится
/// <see cref="Auto"/>, а не роняет окно и не снимает копию «не тем» режимом молча.
/// </summary>
public static class BackupScopeDecisions
{
    /// <summary>Автоматический режим — умолчание и ответ на мусор в файле настроек.</summary>
    public const string Auto = "auto";

    /// <summary>Полный режим: ничего не пропускать.</summary>
    public const string Full = "full";

    /// <summary>Свой фильтр: автоматический плюс названные имена.</summary>
    public const string Custom = "custom";

    /// <summary>
    /// Порядок режимов в списке окна копий — он же порядок значений в файле и порядок членов
    /// <see cref="BackupScope"/>. Один список на всю панель: окно ставит выбор по НОМЕРУ
    /// (<see cref="IndexOf"/>), а не сравнением подписей, и разойтись подписи с значениями
    /// поэтому не могут.
    /// </summary>
    public static readonly string[] All = { Auto, Full, Custom };

    /// <summary>Имя режима для файла настроек. Неизвестный член перечисления даёт <c>auto</c>.</summary>
    public static string Token(BackupScope scope) => scope switch
    {
        BackupScope.Full => Full,
        BackupScope.Custom => Custom,
        _ => Auto,
    };

    /// <summary>
    /// Значение из файла настроек в сравнимом виде. Неизвестное (в том числе пустое и мусор) —
    /// <c>auto</c>: файл правят руками, и «непонятное» обязано означать поведение, которое было
    /// у панели до появления режимов, а не третий, никем не выбранный режим.
    /// </summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Array.IndexOf(All, text) >= 0 ? text : Auto;
    }

    /// <summary>Режим по значению из файла. Мусор — <see cref="BackupScope.Auto"/>.</summary>
    public static BackupScope Parse(string? value) => Normalize(value) switch
    {
        Full => BackupScope.Full,
        Custom => BackupScope.Custom,
        _ => BackupScope.Auto,
    };

    /// <summary>Номер режима в списке окна — по нему окно ставит выбор, не сравнивая строки.</summary>
    public static int IndexOf(BackupScope scope)
    {
        var index = Array.IndexOf(All, Token(scope));
        return index < 0 ? 0 : index;
    }

    /// <summary>Режим по номеру в списке. Номер вне списка даёт автоматический, а не исключение.</summary>
    public static BackupScope FromIndex(int index) =>
        index >= 0 && index < All.Length ? Parse(All[index]) : BackupScope.Auto;

    /// <summary>
    /// Лишние имена папок в сравнимом виде: без пустых, без лишних пробелов и без повторов
    /// (Windows не различает регистр в именах папок). Порядок СОХРАНЯЕТСЯ — он виден человеку
    /// в окне, и перестановка выглядела бы как «панель переписала мой список».
    ///
    /// Тем же правилом нормализуются каталоги ключей (<c>PanelSettings.NormalizeKeyDirs</c>):
    /// оба списка — это имена и пути, которые человек пишет руками в одну строку.
    /// </summary>
    public static List<string> NormalizeExclusions(IEnumerable<string>? names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var name in names ?? Enumerable.Empty<string>())
        {
            var text = (name ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            if (!seen.Add(text)) continue;

            result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// Список пропуска для корня: обычный плюс названные человеком имена. Пустой результат —
    /// <c>null</c>, то есть «своего списка нет»: так значение уходит в <see cref="BackupSource"/>,
    /// где <c>null</c> и означает «общий список прогона».
    /// </summary>
    public static IReadOnlyList<string>? Merge(IReadOnlyList<string>? normal, IReadOnlyList<string>? extra)
    {
        var merged = new List<string>();

        foreach (var name in normal ?? Array.Empty<string>()) Add(name);
        foreach (var name in extra ?? Array.Empty<string>()) Add(name);

        return merged.Count == 0 ? null : merged;

        void Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (merged.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            merged.Add(name);
        }
    }
}

/// <summary>
/// Сколько примерно займут ДАННЫЕ копии в этом режиме: сумма размеров файлов, которые попадут
/// в архив (не размер самого архива — его сжатие заранее не посчитать).
///
/// <see cref="Known"/> ложен, когда посчитать не удалось или нельзя: в прогоне проверки обход
/// каталогов не делается вовсе (красная линия 4 — прогон не читает данные владельца), а обход
/// чужого дерева может и не пережить отказ доступа. Ложь вместо честного «не знаю» здесь
/// недопустима: по этой цифре человек решает, снимать ли полную копию.
/// </summary>
public sealed record BackupEstimate(bool Known, long Bytes);
