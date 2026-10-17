using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshPanel.Backup;
using DshPanel.Isolation;

namespace DshPanel.Server;

/// <summary>
/// ОДНО СОПРЯЖЁННОЕ УСТРОЙСТВО. <see cref="Id"/> — то, чем панель различает телефоны
/// (у движка различить их нечем вовсе: он не знает, кто к нему подключён, — замер
/// <c>docs\PHONE-F0.md</c> §5.4), <see cref="AddedAt"/> и <see cref="LastSeen"/> —
/// отметки в том же круговом виде, что у цен (<see cref="Pricing.PricingDecisions.Stamp"/>).
///
/// ⚠️ <see cref="Address"/> здесь ЕСТЬ, но наружу не выходит: он нужен панели, чтобы самой
/// показать «когда подключался» и заметить смену адреса, а телефону он ни к чему — это его
/// собственная примета, и отдавать её обратно незачем (см. <see cref="PhoneDevice.Safe"/>).
/// </summary>
public sealed record PhoneDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("addedAt")] string AddedAt,
    [property: JsonPropertyName("lastSeen")] string LastSeen,
    [property: JsonPropertyName("name")] string Name)
{
    /// <summary>Строка устройства, какой её ВИДИТ телефон: без адреса и без чего-либо ещё лишнего.</summary>
    public PhoneDeviceView Safe() => new(Id, AddedAt, LastSeen, Name);
}

/// <summary>
/// Устройство, каким его видно СНАРУЖИ. Отдельный тип, а не «тот же, но с пустым адресом»:
/// пустая строка однажды нашлась бы в чужом коде как «адрес неизвестен», и адрес вернулся бы
/// в вывод. Здесь поля адреса нет вовсе — вернуть его наружу нечем.
///
/// <see cref="Name"/> — КОРОТКОЕ ИМЯ ТЕЛЕФОНА ИЗ <c>User-Agent</c> (решение владельца 12.10.2026),
/// разобранное при сопряжении (<see cref="PhoneDecisions.DeviceName"/>). Пусто — значит телефон
/// себя не назвал: и страница, и панель скажут «телефон», а не выдумают имя.
/// </summary>
public sealed record PhoneDeviceView(string Id, string AddedAt, string LastSeen, string Name = "");

/// <summary>
/// ДВЕРЬ К ФАЙЛУ ПРОПУСКА: чтение и запись приходят делегатами, а не путём.
///
/// Устроена как <see cref="ServerStateFiles"/> и по той же причине. Контроллер калитки не знает,
/// где лежит файл: прогон проверки на машине владельца иначе прочитал бы НАСТОЯЩИЙ пропуск
/// человека и (хуже) мог бы признать чужое устройство своим. Путь выдаёт тот, у кого есть право
/// на данные панели (<see cref="RunRights.LocalData"/>), и только он.
///
/// Второе следствие того же решения: у прогона без права все делегаты — пустышки
/// (<see cref="None"/>), и <see cref="Persists"/> у них <c>false</c>. Без этого признака панель
/// писала бы «сохранено», не сохранив ничего.
/// </summary>
public sealed record PhonePassFiles(Func<string> Read, Action<string> Write)
{
    /// <summary>Есть ли КУДА писать. <c>false</c> — прогон без права: пропуска нет и быть не может.</summary>
    public bool Persists { get; init; } = true;

    /// <summary>
    /// КУДА СКАЗАТЬ ПРИЧИНУ ОТКАЗА чтения или записи пропуска. Пусто — прогон без права: там
    /// отказ не беда, а устройство (см. <see cref="None"/>).
    ///
    /// ⚠️ **Зачем причина.** `Save` возвращал <c>false</c> и МОЛЧАЛ: сопряжение срывалось
    /// пятисоткой, а в журнале не было ни строки — поэтому мерцание `PhoneLookTests` из цикла C1
    /// осталось загадкой на четыре зелёных прогона (`HISTORY.md`, долг CH-01). Отказ без причины
    /// нельзя ни доказать, ни опровергнуть: «зелено» неотличимо от «повезло».
    /// </summary>
    public Action<string>? Log { get; init; }

    /// <summary>Права на файл нет. Пусто и молча — как у файлов состояния сервера.</summary>
    public static PhonePassFiles None { get; } = new(() => string.Empty, _ => { }) { Persists = false };

    /// <summary>
    /// Файл ПОД ЭТИМ КОРНЕМ — то, что получает прогон с правом на данные панели. Путь берётся
    /// у <see cref="AppPaths"/>: второго места, где он вычисляется, быть не должно.
    ///
    /// <paramref name="log"/> — куда сказать, если права «только владелец» поставить не удалось.
    /// Отказ в правах запись НЕ отменяет (пропуск нужен работающей калитке), но и молчать о нём
    /// нельзя: «файл с секретом записан, но открыт всем» — это не «всё хорошо».
    /// </summary>
    public static PhonePassFiles Under(AppPaths paths, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        return new PhonePassFiles(
            Read: () => ReadText(paths.PhonePassFile, log),
            Write: text => SaveRestricted(paths.PhonePassFile, text, log))
        {
            Log = log,
        };
    }

    /// <summary>
    /// ОТКРЫТЬ ФАЙЛ ПРОПУСКА НА ЧТЕНИЕ — тем же способом, каким его читает панель, и этим же
    /// способом его открывает проверка, которая держит файл, пока идёт запись.
    ///
    /// ⚠️ **`FileShare.ReadWrite | FileShare.Delete` — здесь не украшение, а починка дефекта.**
    /// `File.ReadAllText` (как было) открывает файл с `FileShare.Read`, то есть **пускает только
    /// читателей**: пока рендер раздела настроек или состояние значка читают пропуск, `File.WriteAllText`
    /// сопряжения получает отказ совместного доступа, `Save` возвращает <c>false</c>, и человек видит
    /// **500 вместо входа**. Читателей много и они частые (файл читается на КАЖДЫЙ рендер —
    /// <c>PhoneGate.Devices</c>/<c>DeviceCount</c>), а писателей три (сопряжение, отвязать одно
    /// устройство, отвязать все), и все три шли мимо замка. Отсюда мерцание `PhoneLookTests`.
    /// Разрешение на запись и удаление снимает саму возможность этого отказа: читатель больше
    /// не запирает файл.
    /// </summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Прочитать файл пропуска целиком. Нет файла — это «нечего», а не сбой.</summary>
    private static string ReadText(string path, Action<string> log)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;

            using var stream = OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception error)
        {
            // Для вызывающего это «пропуска нет», и калитка закрыта. Но отказ обязан назвать
            // причину: молчание здесь означало бы «не сопряжено» без повода и без следа.
            Say(log, Shell.PanelStrings.PhonePassReadFailedLogFormat, error);

            return string.Empty;
        }
    }

    /// <summary>
    /// Записать файл пропуска: без BOM (как все файлы панели) и СРАЗУ с правами «только владелец».
    ///
    /// Права ставятся тем же способом, каким закрывается ссылка входа и архив с ключами
    /// (<see cref="BackupEngine.RestrictToOwner"/>) — второго такого кода в панели нет.
    ///
    /// ⚠️ **Файл пишется во ВРЕМЕННЫЙ рядом и заменяется АТОМАРНО** (<see cref="File.Replace"/>,
    /// а он зовёт системную `ReplaceFile`). Замер 17.10.2026 на этой машине, по которому выбрано
    /// именно так:
    ///
    /// * читатель держит файл с разрешением на запись и удаление (<see cref="OpenRead"/>) →
    ///   **замена проходит**, и **открытый дескриптор читателя продолжает видеть ПРЕЖНЕЕ
    ///   содержимое целиком**: читатель не поймает обрезанный файл, а новый читатель сразу увидит
    ///   новое. Это и есть атомарность на уровне того, кто читает;
    /// * читатель держит файл как `File.ReadAllText` (пускает только читателей) → отказ; такого
    ///   читателя в панели больше нет — ровно в этом и была причина дефекта CH-01;
    /// * простое переименование поверх (`File.Move(…, overwrite: true)`) **не годится**: оно
    ///   получает «отказано в доступе» даже когда файл открыт с разрешением на удаление — замер
    ///   там же, и первая редакция этой правки на нём и споткнулась.
    ///
    /// Отказ записи исключением НЕ оборачивается: неудавшаяся запись пропуска не имеет права
    /// уронить панель, а вызывающий узнаёт о ней по <c>false</c> от <see cref="PhonePass.Save"/>
    /// и по строке причины в журнале (её пишет <see cref="Save"/>).
    /// </summary>
    private static void SaveRestricted(string path, string text, Action<string> log)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Временные файлы — рядом с целевым: замена через границу тома неатомарна и падает.
        var temp = path + ".new";
        var backup = path + ".old";

        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Права ставятся ВРЕМЕННОМУ файлу, ДО замены: иначе между записью и правами файл
            // с секретом успел бы полежать открытым всем.
            Restricted(temp, log);

            if (File.Exists(path)) Replace(temp, path, backup);
            else File.Move(temp, path);

            // И ЦЕЛЕВОМУ — после: замена сохраняет снимок свойств прежнего файла.
            Restricted(path, log);
        }
        catch
        {
            // Недописанный временный файл не оставляем: следующий проход иначе нашёл бы мусор.
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // И это не беда: главное — отказ назван вызывающему.
            }

            throw;
        }
    }

    /// <summary>
    /// ЗАМЕНА С ПОДМЕНОЙ ИМЕНИ: новый файл занимает имя прежнего одним действием.
    ///
    /// Копия прежнего файла (<paramref name="backup"/>) нужна не нам — её требует сама операция:
    /// без неё замена поверх открытого файла отказывает (замер 17.10.2026). Копия удаляется сразу:
    /// в ней тот же секрет, и держать его лишним файлом на диске незачем.
    /// </summary>
    private static void Replace(string temp, string path, string backup)
    {
        try
        {
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch
            {
                // Копия осталась — это не отказ записи: пропуск уже на месте.
            }
        }
    }

    /// <summary>Права «только владелец» на файл; не вышло — сказать в журнал, но запись не отменять.</summary>
    private static void Restricted(string path, Action<string> log)
    {
        var restricted = BackupEngine.RestrictToOwner(path);
        if (restricted.Ok) return;

        log(string.Format(
            CultureInfo.CurrentCulture,
            Shell.PanelStrings.SrvEntryLinkRightsFailedFormat,
            restricted.Reason));
    }

    /// <summary>
    /// СКАЗАТЬ ПРИЧИНУ ОТКАЗА — И БЕЗ ПУТИ.
    ///
    /// Журнал уезжает в резервную копию и в чужой отчёт (красная линия 7), а текст исключения
    /// ввода-вывода содержит **абсолютный путь файла** — то есть каталог пользователя. Поэтому
    /// печатается ВИД отказа и код, а не сообщение целиком: этого хватает, чтобы отличить «файл
    /// занят другим процессом» от «нет прав», и не хватает, чтобы утечь имени пользователя.
    /// </summary>
    internal static void Say(Action<string>? log, string key, Exception error)
    {
        if (log is null) return;

        log(string.Format(CultureInfo.CurrentCulture, key, ReasonOf(error)));
    }

    /// <summary>Вид отказа словами плюс код: «занят другим процессом (0x80070020)».</summary>
    internal static string ReasonOf(Exception error)
    {
        var kind = (error.HResult & 0xFFFF) switch
        {
            // 32 ERROR_SHARING_VIOLATION, 33 ERROR_LOCK_VIOLATION — файл держит другой.
            32 or 33 => Shell.PanelStrings.PhonePassBusyLogReason,

            // 5 ERROR_ACCESS_DENIED — права не те.
            5 => Shell.PanelStrings.PhonePassNoRightsLogReason,

            _ => error.GetType().Name,
        };

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} (0x{1:X8})",
            kind,
            error.HResult);
    }
}

/// <summary>
/// ПРОПУСК ТЕЛЕФОНА — ОТДЕЛЬНЫМ ФАЙЛОМ, с правами владельца, и НЕ в <c>settings.json</c>.
///
/// **Формат — одна строка JSON:** <c>{"version":1,"pass":"&lt;32 случайных байта base64url&gt;",
/// "devices":[{"id":"&lt;uuid&gt;","address":"…","addedAt":"…","lastSeen":"…"}]}</c>. Почему так,
/// а не иначе:
///
///   * **версия в файле** — чтобы будущая смена формата узнавалась, а не угадывалась. Чужая версия
///     читается как ПУСТОЕ состояние: панель честно скажет «сопряжение не пройдено», а не упадёт
///     и не притворится, что пускает;
///   * **разбор ТЕРПИМЫЙ**: нет файла, битый JSON, чужая версия, мусор в полях — пустое состояние,
///     а не исключение. Файл лежит у человека на диске и правится руками; падение панели на его
///     содержимом было бы дефектом, а не защитой (тот же довод, что у настроек);
///   * **пропуск — 32 случайных байта** (<see cref="RandomNumberGenerator"/>) в base64url:
///     столько же, сколько чеканит движок для своей ссылки входа. Ровно столько, сколько нужно,
///     чтобы подобрать его перебором было нечем.
///
/// **Наружу из класса не выходит ни значение пропуска, ни его часть.** Сравнение — только
/// «совпало/нет» (<see cref="Match"/>, постоянное по времени), список устройств — только
/// <see cref="PhoneDeviceView"/> (без адреса). Иначе пропуск однажды уехал бы в окно, в журнал
/// или в отчёт — и перестал бы быть секретом.
/// </summary>
public sealed class PhonePass
{
    /// <summary>
    /// ПОРТ КАЛИТКИ ПО УМОЛЧАНИЮ — <c>43127</c>, решение владельца (не обсуждается).
    ///
    /// Живёт ОДНИМ местом: отсюда его берут и умолчание настроек, и чистка настроек. Второе число
    /// рядом означало бы, что однажды поправят одно из двух, и «порт, который выбрал человек»
    /// разойдётся с «портом, который панель займёт».
    /// </summary>
    public const int DefaultPort = 43127;

    /// <summary>Версия формата файла. Чужая — «сопряжения нет», а не «читаем как поняли».</summary>
    public const int FormatVersion = 1;

    /// <summary>Сколько случайных байт в пропуске. Столько же, сколько у ссылки входа движка.</summary>
    public const int TokenBytes = 32;

    private readonly string _token;
    private readonly IReadOnlyList<PhoneDevice> _devices;

    private PhonePass(string token, IReadOnlyList<PhoneDevice> devices)
    {
        _token = token;
        _devices = devices;
    }

    /// <summary>Ничего не сопряжено: файла нет, он битый или версия чужая. Калитка в этом состоянии закрыта.</summary>
    public static PhonePass Empty { get; } = new(string.Empty, Array.Empty<PhoneDevice>());

    /// <summary>Есть ли пропуск. <c>false</c> — ни одно устройство не пущено ни при каком коде.</summary>
    public bool Any => _token.Length > 0;

    /// <summary>Сколько устройств сопряжено (само число — не секрет, им панель отвечает на «подключён ли»).</summary>
    public int DeviceCount => _devices.Count;

    /// <summary>
    /// Устройства, какими их видно СНАРУЖИ: без адреса. Это единственный вид, в котором список
    /// покидает класс, — и потому адрес не может утечь «по дороге».
    /// </summary>
    public IReadOnlyList<PhoneDeviceView> SafeDevices => _devices.Select(device => device.Safe()).ToList();

    /// <summary>
    /// Совпал ли предъявленный пропуск с нашим — и НИЧЕГО больше.
    ///
    /// ⚠️ Сравнение ПОСТОЯННОЕ ПО ВРЕМЕНИ (<see cref="CryptographicOperations.FixedTimeEquals"/>
    /// по байтам), а не <c>==</c>. Обычное сравнение строк заканчивается на первом различии, и по
    /// времени ответа пропуск подбирается посимвольно, без всякого перебора. Пропуск без пропуска
    /// не совпадает НИКОГДА, и это отдельная строка, а не следствие сравнения.
    /// </summary>
    public bool Match(string? given)
    {
        if (_token.Length == 0) return false;
        if (string.IsNullOrEmpty(given)) return false;

        return PhoneDecisions.ConstantTimeEquals(given!, _token);
    }

    /// <summary>
    /// СОПРЯЖЕНО ЛИ ЭТО УСТРОЙСТВО — поимённо. Нужно калитке: подпись (ниже) доказывает, что
    /// устройство знает пропуск, а ЭТА проверка — что человек его не отвязал.
    ///
    /// ⚠️ Без неё «отвязать устройство» не значило бы ничего: подпись у отвязанного устройства
    /// осталась бы верной (она считается от того же пропуска), и оно продолжило бы ходить.
    /// </summary>
    public bool HasDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        return _devices.Any(device =>
            string.Equals(device.Id, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ПОДПИСЬ УСТРОЙСТВА — то, что уезжает телефону ВМЕСТО пропуска.
    ///
    /// **Почему не сам пропуск.** Пропуск один на всю панель: утечёт он с телефона (чужой браузер,
    /// снимок экрана, копия cookie) — и пускаться будет уже не одно устройство, а всякий, кто
    /// предъявит этот пропуск. Подпись же — <c>HMAC-SHA256(пропуск, deviceId)</c>: она
    /// **привязана к устройству**, из неё пропуск не выводится (HMAC — необратимая функция),
    /// а отвязка устройства (<see cref="WithoutDevice"/>) закрывает ему вход поимённо.
    ///
    /// ⚠️ Ключ подписи — САМ ПРОПУСК, и это осознанно: отдельного ключа подписи заводить не надо,
    /// а смена пропуска (<see cref="Create"/>) обязана обесценивать все прежние подписи — иначе
    /// «сопрячь заново» не означало бы «старые устройства больше не пускаются».
    ///
    /// Пусто — пропуска нет вовсе: подписывать нечем, и калитка такую подпись не примет.
    /// </summary>
    public string DeviceSignature(string deviceId)
    {
        if (_token.Length == 0 || string.IsNullOrWhiteSpace(deviceId)) return string.Empty;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_token));

        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(deviceId.Trim())));
    }

    /// <summary>
    /// ПРИНИМАТЬ ЛИ ЭТО УСТРОЙСТВО — ДВА УСЛОВИЯ, И ОБА ОБЯЗАТЕЛЬНЫ:
    ///
    /// 1. устройство **есть в списке** (<see cref="HasDevice"/>) — иначе отвязанное устройство
    ///    продолжило бы ходить со своей прежней подписью;
    /// 2. подпись **совпала** — постоянным по времени сравнением, как и всё остальное в пропуске.
    ///    Обычное <c>==</c> здесь означало бы, что подпись подбирается посимвольно по времени ответа.
    ///
    /// Пропуска нет, подпись пуста, устройство чужое — «нет». Это ответ на вопрос «пускать ли»,
    /// и другого ответа у него нет.
    /// </summary>
    public bool VerifyDevice(string deviceId, string? signature)
    {
        if (!HasDevice(deviceId)) return false;
        if (string.IsNullOrEmpty(signature)) return false;

        var expected = DeviceSignature(deviceId);
        if (expected.Length == 0) return false;

        return PhoneDecisions.ConstantTimeEquals(signature!, expected);
    }

    /// <summary>
    /// Прочитать состояние из текста файла. ТЕРПИМО: что угодно непонятное — <see cref="Empty"/>.
    /// Исключений не бросает, поэтому калитка может звать это на каждой попытке входа.
    /// </summary>
    public static PhonePass Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        try
        {
            var file = JsonSerializer.Deserialize<FileFormat>(text!, Reading);
            if (file is null) return Empty;

            // Чужая версия — не «прочитаем как поняли», а «сопряжения нет»: формат мог сменить
            // смысл полей, и пустить по нему устройство значило бы пустить по догадке.
            if (file.Version != FormatVersion) return Empty;

            var token = (file.Pass ?? string.Empty).Trim();
            if (token.Length == 0) return Empty;

            var devices = (file.Devices ?? new List<PhoneDevice>())
                .Where(device => device is not null && !string.IsNullOrWhiteSpace(device.Id))
                .Select(device => device with
                {
                    Id = device.Id.Trim(),
                    Address = (device.Address ?? string.Empty).Trim(),
                    AddedAt = (device.AddedAt ?? string.Empty).Trim(),
                    LastSeen = (device.LastSeen ?? string.Empty).Trim(),

                    // ⚠️ ЗАПИСЬ БЕЗ ИМЕНИ ЧИТАЕТСЯ: поле добавлено 12.10.2026, и прежний файл
                    // человека (или файл, записанный 2.0.3) имени не имеет вовсе. Это не «битый
                    // файл»: имя — подпись для человека, а сопряжение решает пропуск.
                    Name = (device.Name ?? string.Empty).Trim(),
                })
                .ToList();

            return new PhonePass(token, devices);
        }
        catch
        {
            // Битый JSON, чужой тип поля, обрезанный файл — всё это «сопряжения нет».
            return Empty;
        }
    }

    /// <summary>
    /// НОВЫЙ пропуск: случайные <see cref="TokenBytes"/> байт в base64url. Старый пропуск при этом
    /// забывается вместе со списком устройств — сопряжение начинается заново, и это ЦЕЛЬ:
    /// «заново сопрячь» обязано означать «старые устройства больше не пускаются».
    /// </summary>
    public static PhonePass Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        var token = Base64Url(bytes);

        return new PhonePass(token, Array.Empty<PhoneDevice>());
    }

    /// <summary>
    /// ЗАВЕСТИ НОВОЕ УСТРОЙСТВО, сохранив прежний пропуск и прежний список.
    ///
    /// ⚠️ **Именно этим калитка и сопрягает — а не <see cref="Create"/>.** Первая редакция звала
    /// <c>Create().WithDevice(…)</c>, и это значило, что **каждое новое сопряжение стирает прежние
    /// устройства**: у панели пропуск появлялся заново, старые cookie переставали совпадать,
    /// а список «кто подключён» всегда состоял из одного. Владелец увидел бы, что второе
    /// устройство отвязало первое, — и это не «безопасность», а потеря его решения.
    ///
    /// Пропуска нет (файла нет, он битый, версия чужая) — заводится новый (<see cref="Create"/>):
    /// сопрягать в пустоте можно, а <c>Create</c> для этого и существует. Пропуск есть — он
    /// и остаётся: старые устройства обязаны продолжать ходить, пока человек не отвяжет их сам.
    ///
    /// <paramref name="id"/> подставляет вызывающий (калитка зовёт <c>DeviceIdFor</c>): имя
    /// устройства — решение не этого класса, а того, кто знает, что оно значит человеку.
    ///
    /// <paramref name="name"/> — КОРОТКОЕ ИМЯ ТЕЛЕФОНА из <c>User-Agent</c> (решение владельца
    /// 12.10.2026), уже разобранное и обрезанное. Пусто — телефон себя не назвал: так и запишем.
    /// </summary>
    public PhonePass WithNewDevice(string id, string? address, string at, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var basis = _token.Length > 0 ? this : Create();

        return basis.WithDevice(id, address, at, name);
    }

    /// <summary>
    /// Сопряжённое устройство списком: <paramref name="address"/> — адрес телефона, каким его
    /// видела панель. Повторный вход тем же <paramref name="id"/> НЕ заводит вторую запись —
    /// иначе список рос бы на каждое обращение, а «когда подключался» показывало бы не то.
    ///
    /// ⚠️ **Имя при повторном входе обновляется, только если оно пришло:** телефон, вошедший
    /// в другой раз иначе (другой браузер, иная строка), не должен терять уже записанное имя —
    /// «не знаю» не имеет права затирать «знаю».
    /// </summary>
    public PhonePass WithDevice(string id, string? address, string at, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var wanted = id.Trim();
        var fresh = (name ?? string.Empty).Trim();
        var list = new List<PhoneDevice>(_devices.Count + 1);
        var updated = false;

        // Замена НА МЕСТЕ, а не «найти индекс и присвоить»: список устройств невелик, а явный
        // проход читается однозначно — «нашли — обновили, не нашли — добавили».
        foreach (var device in _devices)
        {
            if (!updated && string.Equals(device.Id, wanted, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(device with
                {
                    Address = (address ?? string.Empty).Trim(),
                    LastSeen = at,
                    Name = fresh.Length > 0 ? fresh : device.Name,
                });

                updated = true;
                continue;
            }

            list.Add(device);
        }

        if (!updated) list.Add(new PhoneDevice(wanted, (address ?? string.Empty).Trim(), at, at, fresh));

        return new PhonePass(_token, list);
    }

    /// <summary>Тот же пропуск без названного устройства — «отвязать это устройство».</summary>
    public PhonePass WithoutDevice(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var list = _devices
            .Where(device => !string.Equals(device.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new PhonePass(_token, list);
    }

    /// <summary>
    /// Состояние как ОДНА строка JSON — ровно тот вид, в каком оно лежит в файле.
    /// <c>WriteIndented: false</c> здесь не украшение: формат объявлен одной строкой, и файл
    /// из одной строки читается глазами так же, а пишется без переводов строк внутри.
    /// </summary>
    public string ToJson()
    {
        var file = new FileFormat
        {
            Version = FormatVersion,
            Pass = _token,
            Devices = _devices.ToList(),
        };

        return JsonSerializer.Serialize(file, Writing);
    }

    /// <summary>
    /// Записать состояние через дверь. Возвращает признак успеха; исключений не бросает —
    /// неудавшаяся запись не имеет права уронить панель. Права «только владелец» ставит дверь
    /// (<see cref="PhonePassFiles.Under"/>), а не это место.
    ///
    /// ⚠️ **Отказ обязан назвать причину** (<see cref="PhonePassFiles.Log"/>). Раньше здесь стоял
    /// голый <c>catch</c>, и сопряжение срывалось молча: человек видел 500, а журнал — ничего.
    /// Именно поэтому гонка из C1 жила четыре прогона: нечего было читать.
    /// </summary>
    public bool Save(PhonePassFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (!files.Persists) return false;

        try
        {
            files.Write(ToJson());
            return true;
        }
        catch (Exception error)
        {
            PhonePassFiles.Say(files.Log, Shell.PanelStrings.PhonePassSaveFailedLogFormat, error);

            return false;
        }
    }

    /// <summary>Прочитать состояние через дверь. Прогон без права получает пустое — и это правда.</summary>
    public static PhonePass Load(PhonePassFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return files.Persists ? Parse(files.Read()) : Empty;
    }

    /// <summary>
    /// base64url без выравнивания — тот же алфавит, каким движок чеканит свою ссылку входа
    /// (замер <c>docs\PHONE-LIVE.md</c> §1). Отличается от обычного base64 двумя знаками, и именно
    /// поэтому вынесен в метод: второй реализации алфавита в панели быть не должно.
    /// </summary>
    public static string Base64Url(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Вид файла — только для чтения и записи. Наружу этот тип не выходит.</summary>
    private sealed class FileFormat
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pass")]
        public string? Pass { get; set; }

        [JsonPropertyName("devices")]
        public List<PhoneDevice>? Devices { get; set; }
    }

    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = false,
    };
}
