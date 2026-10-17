using System.Globalization;
using DshPanel.Backup;

namespace DshPanel.Server;

/// <summary>Почему предохранительную копию профиля снять нельзя — ДО всякого действия с диском.</summary>
public enum EngineProfileCopyRefusal
{
    /// <summary>Снимать нечего: дома движка нет.</summary>
    NoProfile,

    /// <summary>Место не подготовилось (нет прав, путь занят файлом, нет места).</summary>
    NoPlace,
}

/// <summary>Решение о копии: куда кладём, чем снимаем и можно ли вообще.</summary>
public sealed record EngineProfileCopyPlan(
    bool Any,
    string Source,
    string Target,
    EngineProfileCopyRefusal Refusal,
    string RefusalKey)
{
    /// <summary>Путь копии — он же имя файла: по нему копию находит человек.</summary>
    public string Folder => Target.Length == 0 ? string.Empty : System.IO.Path.GetDirectoryName(Target) ?? string.Empty;
}

/// <summary>
/// ПРЕДОХРАНИТЕЛЬНАЯ КОПИЯ ПРОФИЛЯ ПЕРЕД ПОДМЕНОЙ ВЕРСИИ ДВИЖКА (пункт 4 плана E3.2).
///
/// **Зачем она и почему «либо целая, либо её нет».** Перед обновлением движка панель снимает копию
/// дома движка (сессии, настройки, ключи агентов, плагины). Замер разведки: копия профиля с работой —
/// **42,2 МБ за 1,6 с**, а при подложенной беде панель **отказывает словами** («лежит ФАЙЛ, а нужна
/// папка») или говорит «файл НЕ ПРОЧИТАН: IOException». Отсюда правило: копия, выглядящая как право
/// откатиться, но снятая наполовину, опаснее её отсутствия — **обновление не начинается**.
///
/// **Имя — из семейства, которое ротация не трогает.** Копия называется
/// `<c>dsh2-before-engine-…zip</c>`, и это **второй предохранительный префикс** рядом с
/// `dsh2-before-restore-`: ротация такие файлы не удаляет **по правилу** (они не входят в предел
/// `keep` и в список удаляемых), а не «потому что не узнала». Проверка
/// `Копия_перед_движком_не_убирается_ротацией` стережёт именно это.
///
/// ⚠️ Здесь **нет ни одного действия**: решение о копии (что снимаем, куда и можно ли) —
/// чистое, а сама упаковка идёт существующей дверью 7-Zip в том, кто эту дверь зовёт.
/// </summary>
public static class EngineProfileCopy
{
    /// <summary>Ключи слов: имена, фразы живут в трёх словарях.</summary>
    public static class Words
    {
        public const string NoProfile = "EngineProfileCopyNoProfile";
        public const string NoPlace = "EngineProfileCopyNoPlace";
    }

    /// <summary>
    /// РЕШИТЬ, ЧТО СНИМАЕМ И КУДА. <paramref name="profileDir"/> — дом движка
    /// (<c>AppPaths.DshHome</c>), <paramref name="backupsDir"/> — папка копий панели.
    /// </summary>
    public static EngineProfileCopyPlan Decide(string? profileDir, string? backupsDir, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileDir))
            return Refuse(EngineProfileCopyRefusal.NoProfile, Words.NoProfile);

        if (string.IsNullOrWhiteSpace(backupsDir))
            return Refuse(EngineProfileCopyRefusal.NoPlace, Words.NoPlace);

        return new EngineProfileCopyPlan(
            true,
            profileDir.Trim(),
            System.IO.Path.Combine(backupsDir.Trim(), Name(now)),
            EngineProfileCopyRefusal.NoProfile,
            string.Empty);
    }

    /// <summary>
    /// ИМЯ КОПИИ: <c>dsh2-before-engine-ГГГГ-ММ-ДД-ЧЧММСС.zip</c> — как у копий панели, чтобы человек
    /// видел время по имени, и с префиксом, который ротация не трогает.
    /// </summary>
    public static string Name(DateTimeOffset now) =>
        Prefix + now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>Префикс копии перед подменой движка — ВТОРОЙ предохранительный (см. правило уборки).</summary>
    public const string Prefix = "dsh2-before-engine-";

    /// <summary>Вид файла для правил уборки: предохранительная копия — её не удаляют никогда.</summary>
    public static BackupFileKind Kind(string? name) => BackupNaming.Kind(name);

    /// <summary>
    /// КОПИЯ НЕ СНЯЛАСЬ — ОБНОВЛЕНИЕ НЕ НАЧИНАЕТСЯ. Один ответ на все причины: человеку важно
    /// не «какой код ошибки», а что вернуться к прежней работе будет нечем.
    /// </summary>
    public static EngineProfileCopyPlan Failed() =>
        Refuse(EngineProfileCopyRefusal.NoPlace, EngineUpdateFlow.Words.CopyNotWhole);

    private static EngineProfileCopyPlan Refuse(EngineProfileCopyRefusal refusal, string key) =>
        new(false, string.Empty, string.Empty, refusal, key);
}
