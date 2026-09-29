using DshPanel.Shell;
using System.Globalization;
namespace DshPanel.Backup;

/// <summary>
/// Один корень копии: что именно кладём в архив и под каким именем это в нём лежит.
///
/// <paramref name="Prefix"/> — имя группы ВНУТРИ архива, а не путь на диске. Разделитель —
/// косая черта (<c>/</c>), как принято в zip; вложенность (<c>keys/1-ssh</c>) допустима, но
/// тогда быстрый путь через 7-Zip закрыт (см. <see cref="ZipLayout.CanSevenZip"/>).
/// Поэтому в 2.0 имя группы берётся **именем папки на диске**: раскладку для наката всё равно
/// несёт опись, а имена понятий (v1 звала их <c>dsh-home</c>, <c>engine/npm</c>) закрывают
/// быстрый путь на всякой полной копии. Состав корней и это правило — <c>Backup\BackupPlan.cs</c>.
///
/// <paramref name="Everything"/> — не применять ОБЩИЙ список пропуска: так кладутся движок и Node,
/// потому что там <c>node_modules</c> и есть содержимое, и выбрасывать его нельзя.
/// Свой список пропуска, если он задан, действует и здесь — он задан явно, а не унаследован.
///
/// <paramref name="SkipDirectoryNames"/> — СВОЙ список пропуска для этого корня (пусто — общий,
/// заданный прогону). Нужен там, где общее правило не годится: в домашнем каталоге движка
/// <c>node_modules</c> пропускать нельзя — там свои зависимости профиля.
///
/// <paramref name="WholeDirectoryNames"/> — каталоги, которые берутся ЦЕЛИКОМ, даже если их имя
/// стоит в списке пропуска, и внутри которых список пропуска не действует вовсе. Это про
/// <c>.git</c>: его требует оставить `ROADMAP.md`, а общее правило мусора его выбрасывает.
/// Взять «просто не пропускать» мало: внутри <c>.git</c> лежит свой <c>logs</c> (reflog), и общее
/// правило выбросило бы его — то есть история сохранилась бы не вся.
///
/// <paramref name="SkipFileNames"/> — имена ФАЙЛОВ, которые в этот корень не кладутся. Появились
/// 26.09.2026 вместе с решением владельца о «копии для передачи»: файл ключей доступа
/// (<c>.credentials.yaml</c>) не должен уехать в архив, которым делятся. До этого пропуск был
/// только по именам каталогов, и «не класть один файл» сказать было нечем.
///
/// ⚠️ Сверка идёт ПО ИМЕНИ на любом уровне внутри ЭТОГО корня (так же, как пропуск каталогов,
/// и по той же причине: 7-Zip умеет исключать только по имени). Поэтому список задаётся точечно —
/// там, где имя означает ровно то, что нужно: у корня домашнего каталога движка.
/// </summary>
public sealed record BackupSource(
    string Directory,
    string Prefix,
    bool Everything = false,
    IReadOnlyList<string>? SkipDirectoryNames = null,
    IReadOnlyList<string>? WholeDirectoryNames = null,
    IReadOnlyList<string>? SkipFileNames = null)
{
    /// <summary>
    /// Действующий список пропуска. Порядок именно такой, и это не мелочь: **свой список главнее
    /// «обходить целиком»**. Иначе правило, дописанное корню движка (например «папка копий внутри
    /// него в копию не едет»), молча не сработало бы — а молча не сработавшее правило хуже
    /// отсутствующего.
    /// </summary>
    public IReadOnlyList<string>? SkipFor(IReadOnlyList<string>? runDefault) =>
        SkipDirectoryNames ?? (Everything ? null : runDefault);
}

/// <summary>Может ли 7-Zip разложить этот набор источников. <see cref="Reason"/> — словами, для журнала.</summary>
public sealed record SevenZipFit(bool Can, string Reason);

/// <summary>Чем писать архив.</summary>
public enum ZipWriterKind
{
    /// <summary>7-Zip: быстрый путь. 3,2 с на движок в 213 МБ против 5,8 с у своего обхода (замер).</summary>
    SevenZip,

    /// <summary>Своими силами: обход дерева плюс <c>System.IO.Compression.ZipArchive</c>.</summary>
    BuiltIn,
}

/// <summary>Выбранный способ писать архив и почему именно он.</summary>
public sealed record ZipWriterChoice(ZipWriterKind Writer, string Reason);

/// <summary>
/// ПРАВИЛА РАСКЛАДКИ АРХИВА — чистые решения, без единого касания диска.
///
/// Зачем отдельный класс. Выбор формата архива — решение владельца 25.09.2026: **контейнер ZIP,
/// создаёт 7-Zip, распаковывает сама панель** (у .NET нет чтения 7z, а ZIP читается средствами
/// платформы — значит накат не зависит от чужих программ).
///
/// И тут же вылезло ограничение, добытое пробой, а не догадкой (25.09.2026, `7z.exe` 26.03):
/// **7-Zip хранит путь РОВНО так, как он задан относительно своего текущего каталога**, и
/// другого имени дать не умеет. Проверено тремя запусками на одном и том же дереве:
///
/// | Что задано | Что легло в архив |
/// |---|---|
/// | рабочий каталог — родитель, аргумент — имя папки | <c>src\a.txt</c> — префикс есть, он равен имени папки |
/// | рабочий каталог — сама папка, аргумент <c>*</c> | <c>a.txt</c> — префикса нет вовсе |
/// | рабочий каталог — сама папка, аргумент <c>.</c> | <c>a.txt</c> — то же самое |
///
/// Отсюда правило: **7-Zip годится только там, где имя группы совпадает с именем папки на диске**
/// (и оно уникально, и это не корень диска). Произвольное имя — только своим райтером. Это и есть
/// причина, по которой в проекте живут ОБА способа, а не один: 7-Zip даёт скорость, свой обход даёт
/// свободу в именах и не требует ничего снаружи.
///
/// И то же ограничение делает нужной проверку, которую легко забыть: **имя группы обязано совпасть
/// с именем папки ТОЧНО, включая регистр**. Расходится только регистр — 7-Zip положит запись под
/// именем с диска, опись будет искать её под своим, и группа окажется пустой. Это не «мелочь»:
/// так тихо теряется целый корень копии.
/// </summary>
public static class ZipLayout
{
    /// <summary>Опись копии внутри архива. Единственное, что общего у копии и наката.</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>
    /// Имя папки без пути — то, под чем 7-Zip положит корень в архив. Пусто у корня диска
    /// (<c>C:\</c>): у него «имени папки» нет, и в архив его так не положить.
    /// </summary>
    public static string Leaf(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return string.Empty;

        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
            return Path.GetFileName(trimmed);
        }
        catch
        {
            // Кривой путь — не повод падать в решающей функции: скажем «имени нет»,
            // и вызывающий уйдёт на свой обход, который сообщит об ошибке внятно.
            return string.Empty;
        }
    }

    /// <summary>Имя группы в архиве: всегда с косой чертой и без хвостовой.</summary>
    public static string NormalizePrefix(string prefix) =>
        (prefix ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

    /// <summary>
    /// Может ли 7-Zip разложить такой набор. Отказ всегда назван словами: «нельзя» без причины
    /// в отчёте выглядит как поломка, а не как решение.
    /// </summary>
    public static SevenZipFit CanSevenZip(IReadOnlyList<BackupSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0) return new SevenZipFit(false, PanelStrings.ZipLayoutNoSources);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var prefix = NormalizePrefix(source.Prefix);
            if (prefix.Length == 0)
                return new SevenZipFit(false, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipLayoutSourceNoGroupFormat, source.Directory));

            if (prefix.Contains('/'))
                return new SevenZipFit(false, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipLayoutNestedGroupFormat, prefix));

            var leaf = Leaf(source.Directory);
            if (leaf.Length == 0)
                return new SevenZipFit(false, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipLayoutNoFolderNameFormat, source.Directory));

            // Регистр сверяем ТОЧНО: 7-Zip возьмёт имя с диска, и группа разойдётся с описью.
            if (!string.Equals(prefix, leaf, StringComparison.Ordinal))
                return new SevenZipFit(false,
                    string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipLayoutGroupMismatchFormat, prefix, leaf));

            if (!seen.Add(prefix))
                return new SevenZipFit(false, string.Format(CultureInfo.CurrentCulture, PanelStrings.ZipLayoutDuplicateGroupFormat, prefix));
        }

        return new SevenZipFit(true, string.Empty);
    }

    /// <summary>
    /// Чем писать архив. <paramref name="sevenZipPath"/> — найденный 7-Zip или пусто.
    ///
    /// Даже при найденном 7-Zip отказ от него — нормальный ответ, а не поломка: причины названы
    /// в <see cref="CanSevenZip"/>. Обратное тоже верно: **отсутствие 7-Zip не мешает снять копию**,
    /// поэтому копия не может «не сделаться» из-за ненайденной чужой программы.
    /// </summary>
    public static ZipWriterChoice ChooseWriter(IReadOnlyList<BackupSource> sources, string? sevenZipPath)
    {
        var fit = CanSevenZip(sources);

        if (string.IsNullOrWhiteSpace(sevenZipPath))
            return new ZipWriterChoice(ZipWriterKind.BuiltIn, PanelStrings.ZipLayoutNoSevenZip);

        if (!fit.Can)
            return new ZipWriterChoice(ZipWriterKind.BuiltIn, fit.Reason);

        return new ZipWriterChoice(ZipWriterKind.SevenZip, $"7-Zip: {sevenZipPath}");
    }
}
