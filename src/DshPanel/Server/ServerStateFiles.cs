using System.Text;
using DshPanel.Backup;
using DshPanel.Isolation;

namespace DshPanel.Server;

/// <summary>
/// Файлы состояния сервера, которые контроллер читает и пишет: ССЫЛКА ВХОДА и запись
/// «этот сервер наш».
///
/// Почему делегатами, а не путями. Контроллер сервера не знает, где лежат эти файлы, — и это
/// главное свойство типа. Прогон проверки (<c>--shell-selftest</c> идёт ОБЫЧНЫМ путём панели)
/// на машине владельца иначе прочитал бы его настоящие `entry-link.txt` и `own-server.txt`:
/// подсмотрел бы состояние человека и мог бы признать ЕГО сервер своим. Ровно на этом классе
/// дефектов в v1 и обошлась изоляция, поэтому пути выдаёт <c>App.StartPanel</c> — и только
/// по праву <see cref="RunRights.LocalData"/>.
///
/// Второе следствие того же решения: у прогона без права все шесть делегатов — пустышки
/// (<see cref="None"/>), и <see cref="Persists"/> у них <c>false</c>. Это важно не для красоты:
/// без признака панель писала бы в журнал «сохранено», не сохранив ничего, — а журнал в этом
/// проекте читают, чтобы понять, что было НА САМОМ ДЕЛЕ (ровно на такой строке и попался
/// живой прогон самотеста сервера 27.09.2026).
/// </summary>
public sealed record ServerStateFiles(
    Func<string> ReadEntryLink,
    Action<string> WriteEntryLink,
    Action ClearEntryLink,
    Func<string> ReadOwnServer,
    Action<string> WriteOwnServer,
    Action ClearOwnServer)
{
    /// <summary>
    /// Есть ли КУДА писать. <c>false</c> — прогон без права: файлов нет, и панель не имеет права
    /// говорить «сохранил». Читать при этом тоже нечего: обе стороны — следствие одного решения.
    /// </summary>
    public bool Persists { get; init; } = true;

    /// <summary>
    /// Права на файлы нет — например, прогон проверки. **Пусто и молча**, и это осознанно:
    /// строка в журнал на каждое обращение (а состояние перечитывается раз в секунду) утопила бы
    /// в себе настоящие сообщения. Один раз о подавлении говорит тот, кто выдаёт эти делегаты.
    /// </summary>
    public static ServerStateFiles None { get; } = new(
        ReadEntryLink: () => string.Empty,
        WriteEntryLink: _ => { },
        ClearEntryLink: () => { },
        ReadOwnServer: () => string.Empty,
        WriteOwnServer: _ => { },
        ClearOwnServer: () => { })
    {
        Persists = false,
    };

    /// <summary>
    /// Файлы ПОД ЭТИМ КОРНЕМ — то, что получает прогон с правом на данные панели: обычный запуск
    /// человеком и изолированный прогон. Пути берутся у <see cref="AppPaths"/> — второго места,
    /// где они вычисляются, быть не должно, иначе однажды одно из них уедет к чужому каталогу.
    ///
    /// ⚠️ Файлу ССЫЛКИ сразу ставятся права «только владелец»: в ссылке токен входа, и это секрет
    /// (красная линия 7). Права ставятся тем же способом, каким закрывается архив копии с ключами
    /// (<see cref="BackupEngine.RestrictToOwner"/>) — второго такого кода в панели нет.
    ///
    /// Отказ в правах запись НЕ отменяет: ссылка нужна работающей кнопке, а причина уходит строкой
    /// в журнал. Молчать о ней нельзя — «файл записан, но открыт всем» это не «всё хорошо».
    /// </summary>
    public static ServerStateFiles Under(AppPaths paths, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        return new ServerStateFiles(
            ReadEntryLink: () => Read(paths.EntryLinkFile),
            WriteEntryLink: url => SaveEntryLink(paths, url, log),
            ClearEntryLink: () => Delete(paths.EntryLinkFile),
            ReadOwnServer: () => Read(paths.OwnServerFile),
            WriteOwnServer: text => Write(paths.OwnServerFile, text),
            ClearOwnServer: () => Delete(paths.OwnServerFile));
    }

    /// <summary>
    /// Прочитать маленький текстовый файл или вернуть пустое. Открыт наружу ровно для одного
    /// читателя: запасного источника ссылки входа (файл панели 1.x). Путь ему даёт <c>App</c> —
    /// и только по праву машинного окружения, — а правило чтения остаётся здесь одно.
    /// </summary>
    public static string ReadText(string path) => Read(path);

    /// <summary>Прочитать маленький файл или вернуть пустое: нет файла — это «нечего», а не сбой.</summary>
    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            // Файл занят или недоступен — для вызывающего это «значения нет».
            return string.Empty;
        }
    }

    /// <summary>
    /// Записать маленький файл без BOM (как все файлы панели). Отказ — исключением наверх:
    /// вызывающий (контроллер сервера) обязан сказать об этом строкой в журнал, а не проглотить.
    /// </summary>
    private static void Write(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Запись файла ссылки входа вместе с правами «только владелец» (см. <see cref="Under"/>).</summary>
    private static void SaveEntryLink(AppPaths paths, string url, Action<string> log)
    {
        Write(paths.EntryLinkFile, url);

        var restricted = BackupEngine.RestrictToOwner(paths.EntryLinkFile);
        if (restricted.Ok) return;

        log(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Shell.PanelStrings.SrvEntryLinkRightsFailedFormat,
            restricted.Reason));
    }

    /// <summary>Удалить файл, если он есть. Отсутствие файла — не ошибка: цель достигнута.</summary>
    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Файл занят — скажет тот, кому он мешает; здесь это не событие.
        }
    }
}
