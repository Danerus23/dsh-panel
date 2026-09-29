using System.Globalization;
using DshPanel.Shell;

namespace DshPanel.Backup;

/// <summary>
/// Файл в папке копий: имя, момент из ИМЕНИ и размер. Ровно то, что нужно ротации, и ничего
/// больше: опись не читается — чтобы решить «сколько копий храним», открывать каждый архив
/// было бы и медленно, и незачем.
/// </summary>
public sealed record BackupArchive(string Path, DateTimeOffset? CreatedAt, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Вид файла: наша копия, предохранительная копия, копия панели 1.x или чужой файл
    /// (<see cref="BackupNaming.Kind"/>).</summary>
    public BackupFileKind Kind => BackupNaming.Kind(Name);
}

/// <summary>Что ротация собирается удалить и ПОЧЕМУ — словами, для журнала панели.</summary>
public sealed record BackupRotationPlan(IReadOnlyList<BackupArchive> Delete, string Reason);

/// <summary>
/// Чем кончилось удаление: сколько убрано, сколько освободилось и что удалить НЕ удалось.
/// Отдельная запись, а не счётчик, по общему правилу проекта: причина обязана доехать до человека
/// словами, иначе «ротация: удалено 0» ничем не объяснить.
/// </summary>
public sealed record BackupRemoval(int Deleted, long Freed, IReadOnlyList<string> Failed)
{
    public string Summary()
    {
        if (Deleted == 0 && Failed.Count == 0) return string.Empty;

        var size = BackupFormat.Size(Freed);

        return Failed.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, PanelStrings.BackupRotationDoneFormat, Deleted, size)
            : string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupRotationFailedFormat,
                Deleted, size, string.Join(", ", Failed));
    }
}

/// <summary>
/// РОТАЦИЯ КОПИЙ — «хранить N последних, старые удалять». Решение владельца для v2.2, и в нём
/// названы четыре жёстких правила, каждое из случая:
///
/// 1. **самую свежую не удалять никогда.** Иначе панель однажды осталась бы вовсе без копий:
///    «хранить 0» в настройке, пустой список, ошибка в счёте — и удалять становится нечего
///    восстанавливать. Поэтому предел зажимается снизу единицей;
/// 2. **предохранительные копии (<c>dsh2-before-restore-*</c>) не трогать вообще.** Это копия,
///    снятая ПЕРЕД накатом, — последний путь назад для человека, который только что разложил
///    архив поверх рабочей системы. Правило записано и в <see cref="BackupNaming.SafetyPrefix"/>,
///    и здесь: удалить её ротацией значило бы отобрать у человека дорогу назад ровно тогда,
///    когда она нужнее всего;
/// 3. **чужие файлы в папке не трогать.** Папку копий человек выбирает сам, и в ней лежит его
///    собственное: другой архив, документ, что угодно. Ротация смотрит ТОЛЬКО на имена, которые
///    начинаются с наших префиксов (<see cref="BackupNaming.Kind"/>), — и это не проверка
///    «на всякий случай», а способ вообще не иметь списка чужого;
/// 4. **копии панели 1.x не удалять и не считать** (<see cref="BackupFileKind.Legacy"/>). Папка
///    копий у 1.x и 2.0 ОБЩАЯ (решение владельца 26.09.2026), и до этой правки обе панели считали
///    чужие архивы своими: ночью 26.09.2026 панель 1.x сняла свою копию и своей же ротацией
///    удалила архив 2.0 от 24.09. Копия 1.x не идёт ни в счёт «сколько копий храним», ни в список
///    удаляемых — но НАЗЫВАЕТСЯ в причине словами: молча оставленный чужой архив выглядел бы
///    забытым, а человек, увидев его в списке (<see cref="BackupFolder.List"/>), обязан понимать,
///    почему тот не убирается.
///
/// Сверх этого есть пятое правило, тише первых: **копию, у которой время из имени
/// не читается, ротация не удаляет** — по имени она наша, а по возрасту неизвестная, и удалять
/// неизвестное нельзя. Такая копия не идёт и в счёт хранения: она названа в причине отдельно,
/// чтобы её судьба не выглядела решённой молча.
///
/// Класс ЧИСТЫЙ в решающей части: <see cref="Plan"/> не касается диска вовсе (список приходит
/// параметром), поэтому правила перебираются проверками. Диск трогают только <see cref="List"/>
/// (прочитать имена) и <see cref="Apply"/> (удалить названное планом) — и обе называются вызывающим.
/// </summary>
public static class BackupRotation
{
    /// <summary>
    /// Все файлы папки в виде записей ротации. Не нашли папку или не прочитали её — пустой список:
    /// ротация идёт ПОСЛЕ удачной копии и уборкой своей неудачи копию не отменяет. О самой папке
    /// человеку говорит список копий (<see cref="BackupFolder.List"/>, у него причина словами).
    /// </summary>
    public static IReadOnlyList<BackupArchive> List(string folder)
    {
        var files = new List<BackupArchive>();

        try
        {
            if (!Directory.Exists(folder)) return files;

            foreach (var path in Directory.GetFiles(folder))
            {
                long bytes = 0;
                try
                {
                    bytes = new FileInfo(path).Length;
                }
                catch
                {
                    // Размер — украшение причины: без него копия всё равно удаляется.
                }

                files.Add(new BackupArchive(path, BackupNaming.TimeFromName(Path.GetFileName(path)), bytes));
            }
        }
        catch
        {
            // см. комментарий метода: уборка не имеет права уронить копирование.
        }

        return files;
    }

    /// <summary>
    /// Что удалить. Свежие — первыми, лишние сверх предела — в <see cref="BackupRotationPlan.Delete"/>.
    /// </summary>
    /// <param name="files">
    /// Файлы папки; чужие, предохранительные и копии панели 1.x отсеиваются здесь, а не вызывающим.
    /// </param>
    /// <param name="keepCount">Сколько последних копий хранить (границы настройки — 1…20).</param>
    /// <param name="newestName">
    /// Имя копии, которая СЕЙЧАС снята и потому свежая по определению вызывающего. Нужно ровно
    /// для одного случая: у двух копий совпала отметка времени в имени (копии в одну секунду,
    /// ручная правка имени). Без этого «свежая» решалась бы порядком имён, и ротация могла бы
    /// удалить ту, которую человек только что получил.
    /// </param>
    public static BackupRotationPlan Plan(
        IReadOnlyList<BackupArchive> files, int keepCount, string? newestName = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Предел зажат снизу единицей: «хранить 0 последних» означало бы «удалить всё»,
        // а самая свежая копия не удаляется никогда (правило 1 в комментарии класса).
        var keep = Math.Max(1, keepCount);

        var copies = files.Where(file => file.Kind == BackupFileKind.Copy).ToList();
        var dated = copies.Where(file => file.CreatedAt is not null).ToList();
        var undated = copies.Where(file => file.CreatedAt is null).ToList();
        var safety = files.Count(file => file.Kind == BackupFileKind.Safety);

        // Копии панели 1.x считаются ОТДЕЛЬНО и только для того, чтобы назвать их в причине.
        // В предел `keep` они не входят и в `victims` не попадают никогда: папка копий общая,
        // и убирать чужой архив — не наша работа (ночью 26.09.2026 это уже стоило владельцу архива).
        var legacy = files.Count(file => file.Kind == BackupFileKind.Legacy);

        dated.Sort((left, right) => Compare(left, right, newestName));

        var victims = dated.Skip(keep).ToList();
        var parts = new List<string>
        {
            victims.Count == 0
                ? string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BackupRotationNothingFormat, copies.Count, keep)
                : string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BackupRotationPlanFormat,
                    copies.Count, keep, string.Join(", ", victims.Select(Describe))),
        };

        if (safety > 0)
        {
            parts.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupRotationSafetyKeptFormat, safety));
        }

        // Чужие для нас копии 1.x называются числом — и в причине, и в том случае, когда удалять
        // нечего: «ротация ничего не удалила» и «ротация ничего не удалила, а чужих не тронула» —
        // разные новости для человека, который видит эти архивы в списке рядом со своими.
        if (legacy > 0)
        {
            parts.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupRotationLegacyKeptFormat, legacy));
        }

        if (undated.Count > 0)
        {
            parts.Add(string.Format(
                CultureInfo.CurrentCulture, PanelStrings.BackupRotationUndatedFormat,
                string.Join(", ", undated.Select(file => file.Name))));
        }

        return new BackupRotationPlan(victims, string.Join(" · ", parts));
    }

    /// <summary>
    /// Удалить названное планом. Отказ на одном файле не отменяет остальные: копии — это уборка,
    /// а не работа человека, и «не удалилась одна» не повод оставить все.
    /// </summary>
    public static BackupRemoval Apply(BackupRotationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var deleted = 0;
        long freed = 0;
        var failed = new List<string>();

        foreach (var file in plan.Delete)
        {
            try
            {
                File.Delete(file.Path);

                deleted++;
                freed += Math.Max(0, file.Bytes);
            }
            catch (Exception error)
            {
                failed.Add(string.Format(
                    CultureInfo.CurrentCulture, PanelStrings.BackupRotationFailedItemFormat,
                    file.Name, error.GetType().Name));
            }
        }

        return new BackupRemoval(deleted, freed, failed);
    }

    /// <summary>
    /// Порядок «свежие первыми». Три ступени, и каждая решает свой случай:
    /// время (убывание), затем названная вызывающим копия, затем имя.
    /// </summary>
    private static int Compare(BackupArchive left, BackupArchive right, string? newestName)
    {
        var byTime = Nullable.Compare(right.CreatedAt, left.CreatedAt);
        if (byTime != 0) return byTime;

        var leftNamed = Named(left, newestName);
        var rightNamed = Named(right, newestName);
        if (leftNamed != rightNamed) return leftNamed ? -1 : 1;

        // Имя начинается с отметки времени, поэтому порядок имён — тот же «свежие первыми»,
        // что и в списке копий: две сортировки одного списка не должны расходиться.
        return string.CompareOrdinal(right.Name, left.Name);
    }

    private static bool Named(BackupArchive file, string? newestName) =>
        newestName is { Length: > 0 } && string.Equals(file.Name, newestName, StringComparison.OrdinalIgnoreCase);

    private static string Describe(BackupArchive file) => string.Format(
        CultureInfo.CurrentCulture, PanelStrings.BackupRotationItemFormat, file.Name, BackupFormat.Size(file.Bytes));
}
