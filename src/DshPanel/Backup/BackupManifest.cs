using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshPanel.Backup;

/// <summary>
/// ОПИСЬ КОПИИ — единственное, что общего у копии и наката.
///
/// Зачем она внутри архива, а не рядом файлом: копию уносят на флешке, кладут в облако,
/// передают другому человеку. Опись, лежащая ОТДЕЛЬНО, теряется первой — и тогда в архиве
/// остаётся набор каталогов, про который нельзя сказать ни что это, ни куда раскладывать.
/// Поэтому она едет внутри, первой записью.
///
/// Перенесено из v1 «как есть» (шаг 14 порядка переноса, <c>..\dsh-tray\BackupService.cs:126–172</c>),
/// и это не формальность: **имена полей в JSON не менялись**. Опись читает не только эта панель —
/// на ней стоит накат, ею же объясняется состав копии; расхождение имён превратило бы чтение
/// старой копии в «опись не разбирается», а это ровно тот случай, когда человек теряет данные,
/// считая, что они у него есть.
///
/// <see cref="Kind"/> и <see cref="Format"/> — не украшение: по ним накат отличает НАШУ опись
/// от чужого файла, который случайно оказался zip с именем <c>manifest.json</c> внутри.
/// </summary>
public sealed class BackupManifest
{
    /// <summary>Значение <see cref="Kind"/> у нашей описи. Сверяется точно, регистр не щадим.</summary>
    public const string KindValue = "dsh-backup";

    /// <summary>Версия раскладки описи, которую понимает эта панель.</summary>
    public const int SupportedFormat = 1;

    public string Kind { get; set; } = KindValue;

    public int Format { get; set; } = SupportedFormat;

    /// <summary>Когда копия снята, местным временем, строкой «гггг-ММ-дд ЧЧ:мм:сс» (как в v1).</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>Кто снял копию. Имя машины и имя пользователя — личные данные: наружу не выносить.</summary>
    public string App { get; set; } = "DshPanel";

    public string AppVersion { get; set; } = "";

    public string Machine { get; set; } = "";

    public string User { get; set; } = "";

    public string Windows { get; set; } = "";

    public bool WithEngine { get; set; }

    public bool WithSessions { get; set; }

    public bool WithCredentials { get; set; }

    /// <summary>
    /// Откуда что взято: имя группы в архиве → путь на диске. По этой карте накат понимает,
    /// куда раскладывать, и предупреждает, если путь не совпал с нынешним (находка В3).
    ///
    /// ⚠️ Это путь **машины-источника**. На другой машине он другой, поэтому одной карты путей
    /// накату мало — нужны ещё <see cref="Kinds"/>.
    /// </summary>
    public Dictionary<string, string> Paths { get; set; } = new();

    /// <summary>
    /// Что за группа: имя группы в архиве → вид (<c>dsh-home</c>, <c>working-folder</c>,
    /// <c>panel</c>, <c>engine-packages</c>, <c>engine-node</c>).
    ///
    /// Зачем, если есть <see cref="Paths"/>. Затем, что накат на ДРУГОЙ машине не может доверять
    /// записанным путям: копия снята у одного человека, а раскладывается у другого, и «вернуть
    /// сессии» значит положить их в ТЕКУЩИЙ домашний каталог движка, а не в тот, что записан.
    /// Без вида группы накат либо гадал бы по имени папки (а имя группы в 2.0 — как раз имя папки,
    /// см. <see cref="ZipLayout"/>), либо раскладывал бы по чужим путям.
    ///
    /// ⚠️ Поле добавлено 25.09.2026, пока 2.0 не выпущена: читателей прежней раскладки не
    /// существует, а <see cref="Format"/> остаётся 1. Опись БЕЗ этого поля (её писал кто-то
    /// другой) читается: накат тогда раскладывает по записанным путям и говорит об этом словами.
    /// </summary>
    public Dictionary<string, string> Kinds { get; set; } = new();

    public Dictionary<string, string> Versions { get; set; } = new();

    public List<string> Bundles { get; set; } = new();

    public Dictionary<string, string> Plugins { get; set; } = new();

    /// <summary>
    /// Junction-ссылки внутри домашнего каталога движка (профиль ссылается на глобальный движок).
    /// Сами ссылки в архив НЕ кладутся — в описи лежит их список, а накат создаёт их заново
    /// с новыми путями: у ссылки внутри записан абсолютный путь, и в чужой машине он мёртв.
    /// </summary>
    public List<BackupLink> Links { get; set; } = new();

    public long TotalBytes { get; set; }

    public int TotalFiles { get; set; }

    /// <summary>Файлы копии: путь внутри архива, размер и — где он есть — контрольная сумма.</summary>
    public List<BackupFileEntry> Files { get; set; } = new();

    /// <summary>Наша ли это опись. Ответ на «чужой zip» обязан быть «нет», а не «попробуем».</summary>
    public static bool IsOurKind(string? kind) =>
        string.Equals((kind ?? string.Empty).Trim(), KindValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Пишется ровно так, как писала v1: с отступами и без экранирования кириллицы.
    /// Опись читает человек — «\u0442\u043e\u043d\u043a\u0430\u044f» вместо «тонкая» было бы
    /// издевательством, а отступы делают разбор глазами возможным.
    /// </summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Чтение ЧУЖОГО файла: имена полей сверяются без учёта регистра. Запись от этого не
    /// меняется ни на байт — терпимость нужна на входе, а не на выходе: опись мог править
    /// человек или собрать другой инструмент, и отказываться из-за регистра буквы незачем.
    /// </summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Один файл в описи. Путь — имя записи В АРХИВЕ (с прямой косой чертой), а не на диске.</summary>
public sealed class BackupFileEntry
{
    public string Path { get; set; } = "";

    public long Size { get; set; }

    /// <summary>
    /// Контрольная сумма. У движка и Node её НЕТ — там десятки тысяч файлов, а свой CRC
    /// уже лежит в zip; считать SHA-256 по 213 МБ ради проверки, которую делает и распаковщик,
    /// значит удлинить копию в разы. Поэтому поле необязательное и в JSON не пишется, когда пусто.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }
}

/// <summary>Ссылка на каталог: путь внутри копии и то, куда она ведёт (абсолютный путь машины-источника).</summary>
public sealed class BackupLink
{
    public string Path { get; set; } = "";

    public string Target { get; set; } = "";
}
