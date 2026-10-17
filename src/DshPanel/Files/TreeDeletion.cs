namespace DshPanel.Files;

/// <summary>
/// Итог уборки дерева. <see cref="Ok"/> = «дерева больше нет» — и это ПРОВЕРЕНО, а не выведено
/// из того, что вызов не бросил исключение. <see cref="Reason"/> — причина отказа словами
/// (тип исключения .NET и его сообщение; см. примечание в <see cref="TreeDeletion"/>).
/// <see cref="Files"/> и <see cref="Links"/> — сколько чего снято, для журнала и проверки.
/// </summary>
public sealed record TreeRemoval(bool Ok, string Reason, int Files, int Links)
{
    /// <summary>Убирать было нечего — тоже «дерева нет».</summary>
    public static readonly TreeRemoval Nothing = new(true, string.Empty, 0, 0);
}

/// <summary>
/// ЕДИНСТВЕННОЕ место, где панель удаляет дерево каталогов.
///
/// **Зачем оно заведено — замером, а не для порядка.** Проба 10.10.2026 на этой машине
/// (`Directory.Delete(path, recursive: true)` — то, чем панель убирала до сих пор) дала три
/// разных ответа на три разных дерева:
///
///   * **не-ASCII имена** (`Проба-панель-Ω\вложенный каталог\файл-1.txt`) — убралось, исключения
///     нет, отсутствие подтверждено. Беды, из-за которой сосед завёл этот пункт, в .NET НЕТ,
///     и это записано в отчёт как «закрыто словами»;
///   * **junction внутри дерева** — `Access to the path 'ссылка' is denied`, **дерево осталось**.
///     Сама ссылка при этом снята, а цель и её файл ЦЕЛЫ: .NET внутрь ссылки не идёт (грабля
///     «уборка уносит чужое» — из PowerShell 5.1 и `Remove-Item -Recurse`, к .NET не относится);
///   * **файл «только для чтения» внутри дерева** — `Access to the path '…' is denied`, **дерево
///     осталось**. Это тот же тихий остаток, и он не про имена вовсе.
///
/// Опасность в .NET, таким образом, одна: **уборка не удаляет вовсе**, а прежний вызов в
/// `try/catch` с пустым `catch` отвечал на это успехом. Отсюда два правила этого файла:
/// **ссылка снимается сама, внутрь не идём** и **после уборки отсутствие проверяется**.
///
/// **Чего этот файл не делает и делать не должен.** Он не ходит по ссылке (ни рекурсивно, ни
/// ради проверки), не трогает ничего за пределами переданного пути и не удаляет атрибуты, кроме
/// «только для чтения» у файлов ВНУТРИ дерева: без снятия этого атрибута копия панели, сделанная
/// через `File.Copy` (а он переносит атрибуты), делала бы уборку невозможной навсегда — файл
/// остаётся, дерево остаётся, а человек видит только «не удалось».
/// </summary>
public static class TreeDeletion
{
    /// <summary>
    /// Убирает дерево по <paramref name="path"/>: файлы, вложенные каталоги и — первым шагом —
    /// ссылки (junction и symlink) вместо того, чтобы разворачивать их. Возвращает проверенный
    /// итог; исключений наружу не бросает никогда.
    /// </summary>
    public static TreeRemoval Remove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return TreeRemoval.Nothing;

        var files = 0;
        var links = 0;

        try
        {
            if (!Exists(path)) return TreeRemoval.Nothing;

            Strip(path, ref files, ref links);

            // ПРОВЕРКА ПОСЛЕ УБОРКИ. Без неё «удаление удаляет» — это слово, а не факт: и junction,
            // и файл «только для чтения» оставляли дерево на месте, не отличаясь этим от успеха.
            return Exists(path)
                ? new TreeRemoval(false, "дерево осталось на месте", files, links)
                : new TreeRemoval(true, string.Empty, files, links);
        }
        catch (Exception error)
        {
            // Причина от .NET остаётся как есть (тип и сообщение): заменить её нечем — иначе причина
            // исчезнет вовсе. Так же поступает отказ записи файлов замены в `Update\UpdateInstall.cs`.
            return new TreeRemoval(false, $"{error.GetType().Name}: {error.Message}", files, links);
        }
    }

    /// <summary>Есть ли что-нибудь по этому пути — файл, каталог или ССЫЛКА.</summary>
    internal static bool Exists(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) return true;

        // Ссылка, о которой оба `Exists` молчат, спрашивается у атрибутов: `GetAttributes` читает
        // атрибуты САМОЙ ссылки и цели не требует.
        //
        // ⚠️ Замер 10.10.2026: сломанная JUNCTION видна и так — `Directory.Exists` отвечает про неё
        // `true` (точка разбора сама по себе каталог), и это записано здесь потому, что наивное
        // «раз оба Exists молчат, значит мусора нет» на ней как раз и ломается. Молчать оба могут
        // о сломанной ФАЙЛОВОЙ ссылке — а этот случай на этой машине замером НЕ покрыт: завести
        // файловую ссылку нечем (нужны права администратора или режим разработчика).
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ссылка ли это. Атрибуты читаются У САМОЙ ссылки, а не у её цели.</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Strip(string path, ref int files, ref int links)
    {
        if (IsLink(path))
        {
            // Первый шаг и есть ответ на junction: снимаем ССЫЛКУ. Внутрь не идём ни на шаг —
            // цель ссылки панели не принадлежит.
            Detach(path);
            links++;
            return;
        }

        if (!Directory.Exists(path))
        {
            DeleteFile(path, ref files);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsLink(entry))
            {
                Detach(entry);
                links++;
                continue;
            }

            if (Directory.Exists(entry))
            {
                Strip(entry, ref files, ref links);
                continue;
            }

            DeleteFile(entry, ref files);
        }

        Directory.Delete(path, recursive: false);
    }

    /// <summary>
    /// Снимает саму ссылку. Каталог или файл — решает АТРИБУТ ссылки, а не её цель: у сломанной
    /// ссылки цели уже нет, и `<c>Directory.Exists</c>` про неё скажет не то, что нужно
    /// (замер 10.10.2026: сломанная junction отвечает «каталог есть»).
    /// </summary>
    private static void Detach(string path)
    {
        var attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    private static void DeleteFile(string path, ref int files)
    {
        var attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        File.Delete(path);
        files++;
    }
}
