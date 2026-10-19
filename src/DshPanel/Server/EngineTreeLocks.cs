using System.Reflection;

namespace DshPanel.Server;

/// <summary>
/// СКОЛЬКО ЗАМКОВ ДВИЖКА ЗНАЕТ ПАНЕЛЬ И ЧТО С НИМИ ДЕЛАТЬ — одна дверь на всю сборку (задача `t4`
/// плана 2.2.0, блок B). ⚠️ Не путать с <see cref="EngineLocks"/>: там ЗАМКИ ПИСАТЕЛЯ движка
/// (`&lt;файл&gt;.lock`, номер процесса), здесь — ЗАМКИ НАБОРА: точные версии и суммы пакетов.
///
/// **Зачем их несколько.** Замок набора — это «что именно панель поставит человеку». Пока версия
/// движка была одна, замок был один. Переезд на 0.2.x означает **две версии на диске** (решение
/// владельца: держим текущую и прежнюю), а ставить каждую надо по СВОЕМУ замку: у `0.1.5-rc.2`
/// набор — **588** пакетов, у `0.2.1-rc.2` — **683**, и одним замком другой не поставить. Поэтому
/// в сборке теперь два встроенных ресурса, и каждый сверяется со своей закреплённой суммой
/// (<see cref="EngineTreeLock.PinnedSums"/>).
///
/// **Что здесь главное.** Замок, чья сумма не сошлась, в набор НЕ ПОПАДАЕТ: «обновление данных»
/// (или правка файла без правки суммы) обязано кончиться «замка нет», а не установкой неизвестно
/// чего. Замок без версии или не разобравшийся — тоже мимо.
///
/// **Чего здесь нет.** Ни диска рядом с exe, ни сети: замки — встроенные ресурсы сборки, как
/// словари и иконка; ищутся по суффиксу в имени ресурса, а не по хардкоду пути.
/// </summary>
public sealed record EngineTreeLocks(IReadOnlyList<EngineTreeLock> All)
{
    /// <summary>Замков нет вовсе: ни один встроенный файл не прошёл проверку.</summary>
    public static EngineTreeLocks Empty { get; } = new(Array.Empty<EngineTreeLock>());

    /// <summary>Есть ли хоть один замок.</summary>
    public bool Any => All.Count > 0;

    /// <summary>Набор из одного замка — для вызовов, которым второй не нужен (проверки и старые двери).</summary>
    public static EngineTreeLocks Of(EngineTreeLock? single) =>
        single is null || !single.Any ? Empty : new EngineTreeLocks(new[] { single });

    /// <summary>
    /// ВСЕ ВСТРОЕННЫЕ ЗАМКИ, ПРОШЕДШИЕ ПРОВЕРКУ СХЕМЫ И СВОЕЙ СУММЫ. Порядок — от старшей версии
    /// к младшей (глазом читается «текущая/прежняя»); на решения это не влияет — там сверка по версии.
    /// </summary>
    public static EngineTreeLocks Load()
    {
        try
        {
            var assembly = typeof(EngineTreeLocks).Assembly;
            var found = new List<EngineTreeLock>();

            foreach (var resource in Resources(assembly))
            {
                var json = Read(assembly, resource);
                if (json.Length == 0) continue;

                var parsed = EngineTreeLock.Parse(json);
                if (!parsed.Any) continue;

                // ⚠️ СВОЯ СУММА — У КАЖДОГО ЗАМКА СВОЯ: подмена ресурса или правка файла без правки
                // суммы обязана кончиться «этого замка нет», а не установкой неизвестно чего.
                if (!EngineTreeLock.MatchesPinned(json, parsed.EngineVersion)) continue;

                if (found.Any(known => string.Equals(known.EngineVersion, parsed.EngineVersion, StringComparison.OrdinalIgnoreCase)))
                    continue;

                found.Add(parsed);
            }

            if (found.Count == 0) return Empty;

            return new EngineTreeLocks(found
                .OrderByDescending(known => known.EngineVersion, Comparer<string>.Create(EngineNode.Compare))
                .ToList());
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// ЗАМОК ДЛЯ ЭТОЙ ВЕРСИИ ДВИЖКА. Сверка — ПРАВИЛОМ ПАНЕЛИ (`UpdateDecisions.Numeric`): у движка
    /// бывают хвосты `-rc.2`, и сверка строкой не нашла бы свою же версию. Нет замка — пустой замок
    /// (<see cref="EngineTreeLock.Empty"/>), и это честный ответ «ставить не по чему».
    /// </summary>
    public EngineTreeLock For(string? version)
    {
        var wanted = Update.UpdateDecisions.Numeric(version);
        if (wanted.Length == 0) return EngineTreeLock.Empty;

        return All.FirstOrDefault(known => string.Equals(
            Update.UpdateDecisions.Numeric(known.EngineVersion),
            wanted,
            StringComparison.OrdinalIgnoreCase)) ?? EngineTreeLock.Empty;
    }

    /// <summary>Есть ли замок для версии — тот же вопрос, что у <see cref="For"/>, но ответ «да/нет».</summary>
    public bool Has(string? version) => For(version).Any;

    /// <summary>
    /// ЗАМОК, НА КОТОРОМ ПАНЕЛЬ МЕРИЛАСЬ (наша версия движка) — «главный» для тех дверей, которым
    /// версия не называется: проверки установки, порог Node по умолчанию, главная кнопка обновления.
    /// </summary>
    public EngineTreeLock Primary => For(EngineRpcSurface.MeasuredOnVersion);

    /// <summary>Версии движков, для которых есть замок, — списком (для отчёта и для отказа словами).</summary>
    public IReadOnlyList<string> Versions => All.Select(known => known.EngineVersion).ToList();

    /// <summary>Встроенные ресурсы замков: имя ресурса содержит суффикс, а имя сборки не хардкодится.</summary>
    private static IEnumerable<string> Resources(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(name => name.Contains(EngineTreeLock.ResourceSuffix, StringComparison.Ordinal));

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) return string.Empty;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
