using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DshPanel.Localization;
using DshPanel.Shell;

namespace DshPanel.Server;

/// <summary>Род сетевого адаптера, как его видит панель. Род решает, годится ли адрес телефону.</summary>
public enum AddressKind
{
    /// <summary>Ethernet или Wi-Fi — настоящая сеть человека.</summary>
    Physical,

    /// <summary>Туннель (WARP, WireGuard, Tailscale, OpenVPN): адрес есть, но сеть не своя.</summary>
    Tunnel,

    /// <summary>Виртуальный адаптер (Hyper-V, Wi-Fi Direct, Bluetooth, WSL, TAP) — тоже не своя сеть.</summary>
    Virtual,
}

/// <summary>
/// ОДИН АДРЕС МАШИНЫ, каким его видит панель. Поля — ровно то, из чего складывается решение,
/// и ничего лишнего: <see cref="Name"/> — имя адаптера (для слов человеку), <see cref="Ip"/> —
/// сам адрес, <see cref="Kind"/> — род, <see cref="OnLink"/> — признак «подсеть этого адреса
/// ведёт на этот адаптер напрямую» (в той же сети есть кто-то ещё, кроме машины).
///
/// ⚠️ Имя поля — <c>Ip</c>, а не <c>Address</c>: член записи не может называться так же, как её
/// тип (это запрет языка, а не вкус). Заодно читается точнее — речь именно об адресе, а не
/// об адаптере.
///
/// ⚠️ **Метрики интерфейса здесь НЕТ, и это осознанно.** Замер <c>docs\PHONE-F0.md</c> §7.2:
/// у туннеля Cloudflare WARP метрика **5**, у домашнего Ethernet — **25**. Признак «взять меньшую
/// метрику» выбрал бы туннель, то есть адрес, которого телефон в своей сети не увидит.
/// Метрика не передаётся сюда вовсе — на неё нельзя опереться даже по ошибке.
/// </summary>
public sealed record Address(
    string Name,
    string Ip,
    AddressKind Kind,
    bool OnLink);

/// <summary>
/// ЧТО ПАРОВАТЬ В QR. Один кандидат — панель показывает его сама; несколько — выбирает ЧЕЛОВЕК
/// (решение владельца: гадать нельзя); ни одного — панель говорит словами, что показывать нечего.
/// </summary>
public enum AddressChoice
{
    /// <summary>Ни одного подходящего адреса — сказать словами.</summary>
    None,

    /// <summary>Ровно один — показать его.</summary>
    One,

    /// <summary>Несколько — показать все и дать выбрать человеку.</summary>
    Several,
}

/// <summary>
/// РЕШЕНИЕ ОБ АДРЕСЕ: что показывать человеку. <see cref="Candidates"/> — годные адреса
/// (их <see cref="AddressChoice.Several"/> и означает «несколько»), <see cref="Chosen"/> — адрес,
/// когда он один. <see cref="Text"/> собирает слова — так у решения одно место, где оно говорит.
/// </summary>
public sealed record AddressPicked(AddressChoice Choice, IReadOnlyList<Address> Candidates, Address? Chosen)
{
    /// <summary>Слова для человека: годный адрес · «выберите сами» · «показывать нечего».</summary>
    public string Text() => Choice switch
    {
        AddressChoice.One => Loc.T(
            PanelStrings.PhoneAddressOneFormat, Chosen!.Ip, PhoneDecisions.OnLinkWords(Chosen.OnLink)),

        AddressChoice.Several => Loc.T(
            PanelStrings.PhoneAddressManyFormat, Candidates.Count),

        _ => Loc.T(PanelStrings.PhoneAddressNone),
    };
}

/// <summary>Что показывает строка телефона в меню значка. «Не разрешён» — строки нет вовсе.</summary>
public enum PhoneTrayState
{
    /// <summary>Человек не разрешил телефон — в меню значка нет даже строки.</summary>
    NotAllowed,

    /// <summary>Разрешён, но ни одно устройство не подключено — панель ждёт.</summary>
    Waiting,

    /// <summary>Устройство сопряжено и выходило на связь — телефон подключён.</summary>
    Connected,
}

/// <summary>
/// ЧИСТЫЕ РЕШЕНИЯ ТЕЛЕФОНА: код сопряжения, окно его жизни, ограничение попыток, белый список
/// вызовов, выбор адреса для QR и строка состояния в меню значка.
///
/// **Здесь нет ни диска, ни сети, ни портов, ни слушателя.** Всё — функции от данных, поэтому
/// каждое решение проверяется без машины и без движка, а «открыт ли наружу порт» решается не тут,
/// а правом прогона (<see cref="Isolation.RunRights.MayOpenPhoneGate"/>) и калиткой (этап F).
/// Разделение не для красоты: решение, которое нельзя перебрать проверкой, невозможно и доказать.
/// </summary>
public static class PhoneDecisions
{
    /// <summary>Сколько цифр в коде сопряжения. Решение владельца: шесть, и вводит их человек руками.</summary>
    public const int CodeLength = 6;

    /// <summary>
    /// СКОЛЬКО ЖИВЁТ КОД СОПРЯЖЕНИЯ — <b>5 минут</b>. Мера названа здесь, потому что она —
    /// решение, а не случайное число: код диктуют с экрана на телефон, на это уходят десятки секунд;
    /// пяти минут хватает с запасом, а живёт он после этого недолго настолько, чтобы подсмотренный
    /// код не годился через час. Хранить код не нужно вовсе: он умирает раньше, чем панель
    /// закрывается, — и в файле пропуска его нет.
    /// </summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Окно, в котором считаются попытки входа: <b>5 минут</b>.</summary>
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Пауза после того, как с одного адреса набралось <see cref="BlockAfter"/> неудач, — <b>5 минут</b>.
    /// Мера та же, что у окна, и это не совпадение: пауза ровно на окно означает, что «перебор
    /// в лоб» упирается в неё до того, как счёт обнулится.
    /// </summary>
    public static readonly TimeSpan AttemptPause = TimeSpan.FromMinutes(5);

    /// <summary>Лимит неудачных попыток С ОДНОГО АДРЕСА за <see cref="AttemptWindow"/>.</summary>
    public const int AddressLimit = 5;

    /// <summary>Общий лимит неудачных попыток за <see cref="AttemptWindow"/> — на случай, когда адреса меняются.</summary>
    public const int TotalLimit = 20;

    /// <summary>Сколько неудач с одного адреса включают <see cref="AttemptPause"/> (перебор «в лоб»).</summary>
    public const int BlockAfter = 3;

    /// <summary>Адрес, который панель видит у просьбы без адреса. Никогда не совпадает с настоящим.</summary>
    public const string UnknownAddress = "(адрес неизвестен)";

    // ---- код сопряжения -------------------------------------------------------------------

    /// <summary>
    /// НОВЫЙ КОД из <paramref name="length"/> цифр.
    ///
    /// Генерация идёт через ДВЕРЬ (<paramref name="digit"/>) — довод-функцию «дай цифру 0…9»,
    /// а не через <see cref="RandomNumberGenerator"/> внутри. Причина не в красоте: код обязан
    /// проверяться без машины, а проверка, которой нужен генератор случайных чисел, проверяет
    /// генератор, а не правило. Без довода берётся настоящий источник случайности.
    ///
    /// ⚠️ **Ведущие нули сохраняются**: код — строка ровно из <paramref name="length"/> знаков,
    /// а не число. Число «001234» стало бы «1234», и человек, вводящий шесть цифр, не понял бы,
    /// почему код из пяти знаков не принимается.
    /// </summary>
    public static string Code(int length = CodeLength, Func<int, int>? digit = null)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), length, "длина кода обязана быть больше нуля");

        digit ??= _ => RandomNumberGenerator.GetInt32(10);

        var text = new StringBuilder(length);

        for (var index = 0; index < length; index++)
        {
            var value = digit(index);
            if (value is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(digit), value, "цифра обязана быть от 0 до 9");

            text.Append((char)('0' + value));
        }

        return text.ToString();
    }

    /// <summary>
    /// ГОДИТСЯ ЛИ КОД КАК КОД: ровно <paramref name="length"/> знаков и все — цифры.
    ///
    /// Отдельная функция, а не «проверю при сравнении»: иначе код «12» или «12a45б» доходил бы
    /// до сравнения, и отказ был бы неотличим от «не тот код». Здесь эти два отказа различаются
    /// словами, и различает их тот, кто говорит (<see cref="CodeValid"/> против
    /// <see cref="CodeMatches"/>).
    /// </summary>
    public static bool CodeValid(string? code, int length = CodeLength)
    {
        if (code is null || code.Length != length) return false;

        foreach (var symbol in code)
        {
            if (symbol is < '0' or > '9') return false;
        }

        return true;
    }

    /// <summary>
    /// ЖИВ ЛИ КОД: он не истёк и время ещё не пошло вспять. Отметка «когда выпущен» приходит
    /// доводом — значит проверка перебирает и «ровно на границе», и «на секунду позже».
    ///
    /// ⚠️ Граница ВКЛЮЧИТЕЛЬНА: код, которому ровно <see cref="CodeLifetime"/>, ещё жив. Обратное
    /// («жив до истечения») на границе неотличимо от «истёк ровно тогда» и на секунду сокращало бы
    /// жизнь кода, о которой договорились.
    /// </summary>
    public static bool CodeAlive(DateTimeOffset now, DateTimeOffset expiresAt) => now <= expiresAt;

    /// <summary>
    /// СОВПАЛ ЛИ ВВЕДЁННЫЙ КОД С ВЫПУЩЕННЫМ — постоянным по времени сравнением
    /// (<see cref="CryptographicOperations.FixedTimeEquals"/> по байтам), а не <c>==</c>.
    ///
    /// Зачем, если код живёт минуты: обычное сравнение заканчивается на первом различии, и по
    /// времени ответа цифра за цифрой подбирается ВЕСЬ код — шесть цифр перестают быть миллионом
    /// вариантов и становятся шестьюдесятью попытками. Это ровно тот случай, ради которого
    /// постоянное сравнение и существует.
    /// </summary>
    public static bool CodeMatches(string? given, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        if (string.IsNullOrEmpty(given)) return false;

        return ConstantTimeEquals(given!, expected!);
    }

    /// <summary>
    /// СРАВНЕНИЕ ПОСТОЯННОЕ ПО ВРЕМЕНИ — ОДНО место на всю калитку: им сравнивается и код
    /// сопряжения, и сам пропуск (<see cref="PhonePass.Match"/>). Вторая реализация здесь
    /// означала бы, что однажды поправят одну из двух, и одна дверь снова начнёт «звенеть».
    /// </summary>
    public static bool ConstantTimeEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    // ---- ограничение попыток --------------------------------------------------------------

    /// <summary>Одна неудачная попытка входа: с какого адреса и когда.</summary>
    public sealed record Attempt(string Address, DateTimeOffset At);

    /// <summary>Почему не пускаем. <see cref="None"/> — пускаем.</summary>
    public enum AttemptRefusal
    {
        None,

        /// <summary>С этого адреса неудач слишком много.</summary>
        AddressLimit,

        /// <summary>Неудач слишком много всего.</summary>
        TotalLimit,

        /// <summary>С этого адреса только что шёл перебор — пауза ещё не вышла.</summary>
        Paused,
    }

    /// <summary>
    /// РЕШЕНИЕ «ПУСКАТЬ ЛИ ЕЩЁ ПОПЫТКУ». <see cref="Refusal"/> называет причину, <see cref="Wait"/>
    /// — сколько ждать (только у паузы), <see cref="Text"/> говорит словами.
    ///
    /// Считаются ТОЛЬКО НЕУДАЧИ: удачный вход попыток не тратит, иначе человек, зашедший с телефона
    /// десять раз подряд, упёрся бы в лимит на своём же устройстве.
    /// </summary>
    public sealed record AttemptVerdict(AttemptRefusal Refusal, TimeSpan Wait)
    {
        public static AttemptVerdict Allow { get; } = new(AttemptRefusal.None, TimeSpan.Zero);

        public bool Allowed => Refusal == AttemptRefusal.None;

        /// <summary>Слова отказа. Пускаем — пустая строка (как у вердикта гейта умений).</summary>
        public string Text() => Refusal switch
        {
            AttemptRefusal.AddressLimit => Loc.T(PanelStrings.PhoneTooManyFromAddress),
            AttemptRefusal.TotalLimit => Loc.T(PanelStrings.PhoneTooManyTotal),
            AttemptRefusal.Paused => Loc.T(
                PanelStrings.PhoneTryLaterFormat,
                Math.Max(1, (int)Math.Ceiling(Wait.TotalMinutes))),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// ПУСКАТЬ ЛИ ЕЩЁ ОДНУ ПОПЫТКУ. Доводы — только ДАННЫЕ: список неудач, адрес просящего,
    /// «сейчас», окно счёта и мера паузы. Ни таймера, ни памяти процесса здесь нет.
    ///
    /// <paramref name="window"/> и <paramref name="pause"/> — доводы, а не только константы, потому
    /// что проверка обязана уметь назвать ОБА отказа по отдельности: при общих мерах пауза
    /// срабатывает раньше предела на адрес, и «предел спрашивается» иначе не доказать.
    ///
    /// Порядок проверок — от частного к общему, и он же порядок слов: сначала «этот адрес перебирает»,
    /// потом «перебирают все». Первое человеку полезнее: оно про его сеть, а не про мир.
    /// </summary>
    public static AttemptVerdict DecideAttempt(
        IReadOnlyList<Attempt>? attempts,
        string? address,
        DateTimeOffset now,
        TimeSpan? window = null,
        TimeSpan? pause = null)
    {
        var list = attempts ?? Array.Empty<Attempt>();
        var from = now - (window ?? AttemptWindow);
        var wait = pause ?? AttemptPause;

        var mine = list
            .Where(attempt => attempt.At >= from && Same(attempt.Address, address))
            .ToList();

        // ПАУЗА — не «предел», а «отдохни»: она включается раньше предела и говорит, сколько ждать.
        // Отсчёт идёт от ПОСЛЕДНЕЙ неудачи этого адреса и НЕЗАВИСИМО от окна счёта: иначе перебор
        // «по три попытки в окне» обходил бы её вечно (в окно не попал — и паузы нет).
        if (wait > TimeSpan.Zero)
        {
            var recentAll = list.Where(attempt => Same(attempt.Address, address)).ToList();

            if (recentAll.Count > 0)
            {
                var last = recentAll.Max(attempt => attempt.At);

                if (now < last + wait)
                {
                    // Неудач подряд хватает — пауза; меньше — «отдохни» не включается.
                    var recent = recentAll.Count(attempt => attempt.At >= last - wait);

                    if (recent >= BlockAfter)
                        return new AttemptVerdict(AttemptRefusal.Paused, last + wait - now);
                }
            }
        }

        if (mine.Count >= AddressLimit) return new AttemptVerdict(AttemptRefusal.AddressLimit, TimeSpan.Zero);

        if (list.Count(attempt => attempt.At >= from) >= TotalLimit)
            return new AttemptVerdict(AttemptRefusal.TotalLimit, TimeSpan.Zero);

        return AttemptVerdict.Allow;
    }

    /// <summary>Сколько неудач с этого адреса попало в окно — для слов человеку и для проверки.</summary>
    public static int Count(
        IReadOnlyList<Attempt>? attempts,
        string? address,
        DateTimeOffset now,
        TimeSpan? window = null)
    {
        var from = now - (window ?? AttemptWindow);

        return (attempts ?? Array.Empty<Attempt>())
            .Count(attempt => attempt.At >= from && Same(attempt.Address, address));
    }

    /// <summary>
    /// Адрес просящего в сравнимом виде: пусто и пробелы — <see cref="UnknownAddress"/>.
    /// Иначе «адрес не пришёл» и «пришёл адрес из пробелов» были бы двумя разными ключами лимита,
    /// и лимит обходился бы пробелом.
    /// </summary>
    public static string AddressOf(string? address) =>
        string.IsNullOrWhiteSpace(address) ? UnknownAddress : address!.Trim();

    private static bool Same(string? left, string? right) =>
        string.Equals(AddressOf(left), AddressOf(right), StringComparison.OrdinalIgnoreCase);

    // ---- короткое имя телефона из User-Agent (решение владельца 12.10.2026) ----------------

    /// <summary>
    /// ПОТОЛОК КОРОТКОГО ИМЕНИ ТЕЛЕФОНА. Имя — для ГЛАЗА: оно стоит тихой строкой на странице
    /// и строкой в списке подключённых. Длинное имя (некоторые модели объявляют себя целой
    /// фразой) вытесняло бы с экрана телефонного вопроса сам вопрос, поэтому потолок объявлен
    /// числом, а не «на глаз».
    /// </summary>
    public const int DeviceNameLimit = 24;

    /// <summary>
    /// КОРОТКОЕ ИМЯ ТЕЛЕФОНА ИЗ <c>User-Agent</c> — «iPhone», «Pixel 8», «Galaxy S23»…
    ///
    /// **От какого случая.** Решение владельца 12.10.2026: показывать **короткое имя телефона**,
    /// а не внутренний `phone-N`. Источник — <c>User-Agent</c>, который телефон присылает при
    /// сопряжении: это единственное, что он о себе говорит сам.
    ///
    /// ⚠️ **Разбор КОНСЕРВАТИВНЫЙ, и это не лень, а правило:** имя уходит в настройки панели
    /// (файл пропуска) и показывается человеку, поэтому **незнакомая строка даёт пустоту**, а не
    /// «первые два слова» — выдуманное имя хуже отсутствия имени. Пустота — честный ответ,
    /// и страница скажет «телефон» (`PhonePageDeviceFallback`).
    ///
    /// ⚠️ **Это НЕ примета устройства и ничего не решает:** `<c>User-Agent</c> подделывается одной
    /// строкой` (сказано в <c>PhoneGate.DeviceNamePrefix</c>), доступ решает файл пропуска,
    /// а номер устройства — <c>phone-N</c>. Имя здесь — только подпись для человека.
    /// </summary>
    public static string DeviceName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return string.Empty;

        var text = userAgent!;

        // Порядок важен: «CriOS»/«FxiOS» — это тоже iPhone, и модель берётся не из браузера.
        var known = new (string Marker, string Name)[]
        {
            ("iPhone", "iPhone"),
            ("iPad", "iPad"),
            ("Android", string.Empty),   // имя даст модель ниже
            ("Windows Phone", "Windows Phone"),
            ("Macintosh", "Mac"),
        };

        foreach (var (marker, name) in known)
        {
            if (!text.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;

            if (name.Length > 0) return name;

            // ANDROID: настоящее имя — МОДЕЛЬ, и она стоит в скобках после `Android <версия>;`.
            var model = AndroidModel(text);

            return model.Length > 0 ? model : "Android";
        }

        return string.Empty;
    }

    /// <summary>
    /// МОДЕЛЬ ANDROID ИЗ <c>User-Agent</c> — кусок между <c>Android &lt;версия&gt;;</c> и
    /// следующей <c>;</c> или <c>)</c>.
    ///
    /// Пример (замерен не на телефоне владельца, а на обычных строках браузеров):
    /// <c>Mozilla/5.0 (Linux; Android 13; SM-S911B Build/TP1A.220624.014) …</c> → <c>SM-S911B</c>.
    /// Хвост «Build/…» отрезается: он не модель, а сборка.
    /// </summary>
    private static string AndroidModel(string text)
    {
        var marker = text.IndexOf("Android", StringComparison.OrdinalIgnoreCase);

        if (marker < 0) return string.Empty;

        var open = text.IndexOf(';', marker);

        if (open < 0) return string.Empty;

        var close = text.IndexOfAny(new[] { ';', ')' }, open + 1);

        var model = (close < 0 ? text[(open + 1)..] : text[(open + 1)..close]).Trim();

        var build = model.IndexOf("Build", StringComparison.OrdinalIgnoreCase);

        if (build > 0) model = model[..build].Trim();

        // Модель-одно-слово из строчных букв («wv», «mobile») — это не модель, а пометка движка.
        // ⚠️ **И ОДНА БУКВА — ТОЖЕ НЕ МОДЕЛЬ** (живой прогон на телефоне 13.10.2026): Chrome
        // присылает СОКРАЩЁННЫЙ `User-Agent` («Android 10; K»), и «K» попадало в подпись —
        // на странице человек читал «You are signed in as K». Модель настоящего телефона при этом
        // `sdk_gphone64_x86_64` (замер `getprop ro.product.model`). Решение владельца: нераспознанное
        // имя — честное «телефон», а не то, что случайно стоит в строке браузера.
        if (model.Length == 0 || model.Equals("wv", StringComparison.OrdinalIgnoreCase)
            || model.Equals("mobile", StringComparison.OrdinalIgnoreCase)
            || model.Length < 2)
        {
            return string.Empty;
        }

        return model;
    }

    /// <summary>
    /// ИМЯ УСТРОЙСТВА ДЛЯ ЧЕЛОВЕКА: короткое имя, а если его нет — честное «телефон»
    /// (решение владельца 12.10.2026). Внутренний <c>phone-N</c> здесь **не показывается**:
    /// он остаётся в записи панели и в cookie, а человеку названо то, что он узнаёт.
    /// </summary>
    public static string DeviceDisplay(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : Trim(name!, DeviceNameLimit);

    /// <summary>Обрезать по числу знаков, не разрывая слово, если оно рядом.</summary>
    private static string Trim(string text, int limit)
    {
        var value = text.Trim();

        if (value.Length <= limit) return value;

        var cut = value[..limit];
        var space = cut.LastIndexOf(' ');

        return (space > limit / 2 ? cut[..space] : cut).Trim();
    }

    // ---- белый список того, что телефону МОЖНО --------------------------------------------

    /// <summary>
    /// ОДИН РАЗРЕШЁННЫЙ ВЫЗОВ: имя, вид (одиночный или поток) и **имя ключа доводов**, которым
    /// телефон обязан обернуть тело (<c>_request</c> у <c>session/list</c> — замер, не вкус).
    /// </summary>
    public sealed record PhoneCall(string Endpoint, EngineRpcMode Mode, string ArgumentKey);

    /// <summary>
    /// Ключ доводов одиночного вызова у большинства имён. ⚠️ **Доводы телефон НЕ придумывает:**
    /// он шлёт их как есть, а калитка лишь оборачивает тело в конверт движка. Имя ключа здесь —
    /// не «мы подставляем доводы», а «мы знаем, куда положить то, что прислал телефон».
    /// </summary>
    public const string ArgumentRequest = "request";

    /// <summary>
    /// ⚠️ ЕДИНСТВЕННОЕ ИСКЛЮЧЕНИЕ, и оно из ЗАМЕРА, а не из вкуса: у движка `session/list`
    /// объявлен как `session/list(_request)`, и без ключа `_request` он отвечает
    /// `gateway/arguments-invalid` — «missing "_request"» (<c>docs\PHONE-LIVE.md</c> §2).
    /// </summary>
    public const string ArgumentSessionList = "_request";

    /// <summary>Поток событий приложения — канал, по которому приходит вопрос агента.</summary>
    public const string EventsEndpoint = "$events";

    /// <summary>Адрес ответа на вопрос агента — одиночный вызов вне списка 84.</summary>
    public const string EventsResultEndpoint = "$events/result";

    /// <summary>
    /// ОТМЕНА ХОДА АГЕНТА — тот самый вызов, которым вопрос освобождается (замер <c>docs\PHONE-LIVE.md</c> §4:
    /// без ответившего вопрос висит вечно, и снимает его только отмена).
    ///
    /// ⚠️ Имя стоит здесь ОДНИМ местом, потому что им пользуются ДВА пути: белый список
    /// (<see cref="Allowed"/>) — для двери <c>/api/&lt;имя&gt;</c> — и страница телефона (F2),
    /// которая шлёт отмену сама, той же дверью, что и панель. Разойдись эти два имени, отмена
    /// со страницы ушла бы в вызов, которого в белом списке нет.
    /// </summary>
    public const string SessionCancelEndpoint = "session/cancel";

    /// <summary>
    /// ЧТО ТЕЛЕФОНУ МОЖНО — ЗАКРЕПЛЁННЫЙ СПИСОК, ОДНО МЕСТО НА ВСЮ ПАНЕЛЬ.
    ///
    /// **Это не «мост наружу», а белый список, и разница принципиальна.** У движка без входа
    /// открыты статика и `/plugins/events` (замер <c>docs\PHONE-LIVE.md</c> §1), отдающий полную
    /// опись установки, — поэтому калитка обязана пускать ИМЕНА, а не пропускать поток целиком.
    /// Здесь ровно те вызовы, которыми решаются пять действий телефона (открыть разговор, поставить
    /// задачу, увидеть ход, ответить на вопрос агента, отменить), и ничего сверх.
    ///
    /// Чего здесь нет и почему:
    ///
    ///   * **всё, что трогает настройки, ключи и профиль движка** (`settings/*`, `credentials/*`,
    ///     `workspace/*`, `directoryPicker/createDirectory|pick`, `goals/*`, `subagents/*`) — телефон
    ///     не имеет дела ни с ключом модели, ни с профилем (решение владельца: ключ ему не нужен);
    ///   * **`fileUploads/upload` и `session/attachment`** — вложение с телефона это отдельное
    ///     решение и отдельный шаг, а не побочный эффект белого списка;
    ///   * **`llm/discoverModels`** — он ходит в сеть;
    ///   * **статика и `/plugins/*`** — их наружу не отдаём вовсе.
    ///
    /// ⚠️ **Имена `$events` и `$events/result` стоят здесь намеренно, и это граница списка 84**
    /// (замер §3.5 F0): имя с `$` регулярка панели увидеть не может, а ответ на вопрос агента
    /// живёт именно там — без этих двух имён телефон не сможет ответить, и пять действий станут
    /// четырьмя.
    /// </summary>
    public static IReadOnlyList<PhoneCall> Allowed { get; } = new[]
    {
        new PhoneCall("session/list", EngineRpcMode.Unary, ArgumentSessionList),
        new PhoneCall("session/page", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall("session/follow", EngineRpcMode.Stream, ArgumentRequest),
        new PhoneCall("session/control", EngineRpcMode.Stream, ArgumentRequest),
        new PhoneCall("session/create", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall("session/prompt", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall(SessionCancelEndpoint, EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall("session/updateQueue", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall("session/modelCatalog", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall("directoryPicker/list", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall("agentPresets/list", EngineRpcMode.Unary, ArgumentRequest),
        new PhoneCall(EventsEndpoint, EngineRpcMode.Stream, string.Empty),
        new PhoneCall(EventsResultEndpoint, EngineRpcMode.Unary, string.Empty),
    };

    /// <summary>
    /// Вердикт по вызову: пускаем или отказываем СЛОВАМИ. Ключ причины, а не фраза, и текст
    /// собирается здесь же — так у отказа одно место, а переводится он вместе со всей панелью
    /// (то же правило, что у вердикта гейта умений).
    /// </summary>
    public sealed record CallVerdict(bool Allowed, string Endpoint, string ReasonKey)
    {
        /// <summary>Отказ словами. Пускаем — пустая строка.</summary>
        public string Text() => Allowed
            ? string.Empty
            : Loc.T(PanelStrings.PhoneCallRefusedFormat, Endpoint, Loc.T(ReasonKey));
    }

    /// <summary>
    /// ПУСКАТЬ ЛИ ЭТО ИМЯ. Всё, чего нет в <see cref="Allowed"/>, — отказ словами; «похожее имя»
    /// (например `session/listAll`) отказ получает тоже, и это не мелочь: пустить по похожести
    /// значило бы пустить то, чего в списке нет.
    /// </summary>
    public static CallVerdict DecideCall(string? endpoint)
    {
        var wanted = (endpoint ?? string.Empty).Trim();

        if (wanted.Length == 0)
            return new CallVerdict(false, wanted, PanelStrings.PhoneCallNoName);

        var known = Allowed.FirstOrDefault(call => string.Equals(call.Endpoint, wanted, StringComparison.Ordinal));

        return known is null
            ? new CallVerdict(false, wanted, PanelStrings.PhoneCallOutsideList)
            : new CallVerdict(true, wanted, string.Empty);
    }

    /// <summary>
    /// Под каким ключом телефону класть доводы этого вызова. Пусто — доводов у вызова нет вовсе
    /// (поток `$events` открывается пустыми доводами, замер §3).
    /// </summary>
    public static string ArgumentKeyFor(string? endpoint) =>
        Allowed.FirstOrDefault(call => string.Equals(call.Endpoint, (endpoint ?? string.Empty).Trim(), StringComparison.Ordinal))
            ?.ArgumentKey ?? string.Empty;

    // ---- выбор адреса для QR ----------------------------------------------------------------

    /// <summary>
    /// ОДИН АДРЕС, НЕСКОЛЬКО ИЛИ НИ ОДНОГО — и, когда несколько, ВЫБОР ЧЕЛОВЕКА среди них.
    /// </summary>
    /// <remarks>
    /// <see cref="Candidates"/> — годные адреса (их <see cref="AddressChoice.Several"/> и означает
    /// «несколько»), <see cref="Chosen"/> — адрес, когда он один, <see cref="Picked"/> — адрес,
    /// который человек выбрал САМ (пусто, пока не выбирал), <see cref="Address"/> — что взять
    /// для показа и для калитки: выбор человека, иначе единственный годный.
    /// </remarks>
    public sealed record PhoneAddressPicked(
        AddressChoice Choice,
        IReadOnlyList<Address> Candidates,
        Address? Chosen,
        string Picked,
        Func<string, string>? Mask = null)
    {
        /// <summary>
        /// ЧТО ВЗЯТЬ ДЛЯ ПОКАЗА И ДЛЯ QR: единственный годный либо выбор человека — и ВСЕГДА
        /// под маской, когда она есть.
        ///
        /// ⚠️ **Маска стоит ЗДЕСЬ, а не только в <see cref="Text"/>, и это не украшение.**
        /// Прежде выбор человека (`Picked`) не маскировался — и в прогоне съёмки с НЕСКОЛЬКИМИ
        /// подходящими адресами строка на экране была замаскирована, а вот в QR (`Address` уходил
        /// в него напрямую) поехал бы НАСТОЯЩИЙ номер машины: кадр уезжает в README, и это ровно
        /// та утечка, ради которой маска и заведена (решение владельца 11.10.2026). Дефект нашла
        /// проверка `PhoneSectionTests.Под_маской_код_рисуется_из_показанной_строки_и_сказано_словами`
        /// — на этом и стоит правило «QR кодирует ровно ту строку, которую раздел показывает».
        ///
        /// Маска — только ПОКАЗ: отбор годных (<see cref="Suitable"/>) идёт по НАСТОЯЩИМ адресам,
        /// иначе замаскированный адрес отбросили бы как негодный (в нём буква).
        /// </summary>
        public string Address => Picked.Length > 0 ? Show(Picked) : Show(Chosen?.Ip ?? string.Empty);

        /// <summary>
        /// ПОКАЗ ИДЁТ ПОД МАСКОЙ — то есть это прогон съёмки (`--shot`), и адрес на экране
        /// не настоящий. Отдельным ответом, а не догадкой по буквам в строке: по нему окно
        /// и говорит человеку словами, что код на кадре — **образец** (`PhoneQrMasked`),
        /// а кадр уезжает в README (решение владельца 11.10.2026).
        ///
        /// ⚠️ **Рисовать код под маской он НЕ запрещает** — и это решение владельца, а не
        /// недосмотр: «кадр `settings-phone` обязан показать код». В код уходит ровно показанная
        /// строка (`192.168.0.х`), то есть обычный текст без номера машины: утечь из него нечему.
        /// </summary>
        public bool Masked => Mask is not null;

        /// <summary>
        /// Как показать адрес. По умолчанию — как есть; у кадра (`--shot`) сюда приходит маска:
        /// кадр уезжает в README, и номер адреса машины в нём лишний.
        ///
        /// ⚠️ **Маска ложится на ПОКАЗ, а не на отбор.** Замаскированный адрес — уже не адрес
        /// (в нём буква), и правило <see cref="Suitable"/> отбросило бы его как негодный: кадр
        /// показал бы «телефону показывать нечего» вместо адреса.
        /// </summary>
        private string Show(string address) => (Mask ?? (text => text))(address);

        /// <summary>
        /// Слова для человека: годный адрес · «выберите сами» · «показывать нечего».
        /// Собираются из выбора человека, когда он есть: показать «выберите сами» после того,
        /// как человек уже выбрал, значило бы стереть его решение с экрана.
        /// </summary>
        public string Text() => Choice switch
        {
            AddressChoice.One => Loc.T(
                PanelStrings.PhoneAddressOneFormat, Show(Chosen!.Ip), PhoneDecisions.OnLinkWords(Chosen.OnLink)),

            AddressChoice.Several => Picked.Length > 0
                ? Loc.T(PanelStrings.PhoneAddressPickedFormat, Show(Picked))
                : Loc.T(PanelStrings.PhoneAddressManyFormat, Candidates.Count),

            _ => Loc.T(PanelStrings.PhoneAddressNone),
        };
    }

    /// <summary>
    /// ОДИН АДРЕС, НЕСКОЛЬКО ИЛИ НИ ОДНОГО. Гадать панель не имеет права (решение владельца):
    /// подходящих несколько — пусть выберет человек, ни одного — панель говорит словами, что
    /// показывать нечего, вместо выдуманного адреса.
    ///
    /// Порядок кандидатов сохраняется: он приходит от системы, и переставлять его значит показывать
    /// человеку другой список при том же составе.
    ///
    /// ⚠️ Отбор — ТОЛЬКО по <see cref="Suitable"/>, и потому метрика интерфейса в него не входит:
    /// у туннеля WARP она ниже, чем у домашнего адаптера, и правило «взять меньшую» выбрало бы
    /// туннель (замер §7.2). Кандидаты приходят уже отфильтрованными по роду адаптера
    /// (<see cref="Shell.NetworkAddresses.Collect"/>), но и здесь род спрашивается заново:
    /// проверка «в кандидаты попал туннель» зовёт эту функцию напрямую, с туннелем в доводах.
    /// </summary>
    public static PhoneAddressPicked PickAddressChosen(
        IReadOnlyList<Address>? addresses,
        string? picked,
        Func<string, string>? mask = null)
    {
        var suitable = (addresses ?? Array.Empty<Address>()).Where(Suitable).ToList();
        var chosen = (picked ?? string.Empty).Trim();

        // Выбор человека учитывается ТОЛЬКО если он есть среди годных: адрес мог перестать быть
        // годным (адаптер выключили, сеть сменилась), и держать его значило бы посылать телефон
        // туда, где панели больше нет.
        var still = suitable.Any(address => string.Equals(address.Ip, chosen, StringComparison.OrdinalIgnoreCase));
        var kept = still ? chosen : string.Empty;

        return suitable.Count switch
        {
            0 => new PhoneAddressPicked(AddressChoice.None, suitable, null, string.Empty, mask),
            1 => new PhoneAddressPicked(AddressChoice.One, suitable, suitable[0], kept, mask),
            _ => new PhoneAddressPicked(AddressChoice.Several, suitable, null, kept, mask),
        };
    }

    /// <summary>
    /// АДРЕС, НА КОТОРОМ ДОЛЖНА ВСТАТЬ КАЛИТКА (уточнение владельца 11.10.2026, живой прогон:
    /// адрес до калитки не доезжал вовсе — она получала значение, прочитанное при сборке дверей).
    ///
    /// Правило ровно то же, каким панель отвечает человеку в разделе, и это не совпадение:
    /// «по какому адресу телефон найдёт панель» — ОДИН ответ, а не два.
    ///
    /// * **выбор человека**, если он есть среди годных, — им и встаём;
    /// * иначе — **единственный годный** (гадать среди нескольких нельзя, решение владельца);
    /// * иначе — **пусто**: калитка скажет словами, что адреса нет, и не откроется.
    ///
    /// ⚠️ Возвращается НАСТОЯЩИЙ адрес (`Ip`), а не показанный под маской: калитка встаёт
    /// на адрес, а маска — свойство кадра, а не сети.
    /// </summary>
    public static string GateAddress(IReadOnlyList<Address>? candidates, string? chosen)
    {
        var suitable = (candidates ?? Array.Empty<Address>()).Where(Suitable).ToList();
        var pick = (chosen ?? string.Empty).Trim();

        if (pick.Length > 0 &&
            suitable.Any(address => string.Equals(address.Ip, pick, StringComparison.OrdinalIgnoreCase)))
        {
            return pick;
        }

        return suitable.Count == 1 ? suitable[0].Ip : string.Empty;
    }

    /// <summary>
    /// ЧТО ИМЕННО УЕЗЖАЕТ В QR (решение владельца 11.10.2026 после живого прогона).
    ///
    /// **Камера обязана ОТКРЫВАТЬ страницу телефона, а не показывать текст.** Прежний код нёс
    /// только адрес (`192.168.0.5`), и телефон видел просто строку; теперь в код уходит
    /// <c>http://&lt;адрес&gt;:&lt;порт&gt;</c> — адрес со схемой и портом калитки, и камера
    /// открывает страницу сама.
    ///
    /// ⚠️ **Правило «QR кодирует ровно то, что показывает раздел» сохраняется и остаётся
    /// проверяемым** — оно просто стало точнее: кодируется `http://` + ровно та строка адреса,
    /// которую раздел показывает, + `:` + порт калитки, который раздел показывает рядом. Оба
    /// слагаемых видны человеку на экране, поэтому подменить их «на удобное» нельзя.
    ///
    /// Пусто на входе — пусто и на выходе: адреса нет, кодировать нечего, и выдумывать его нельзя.
    /// </summary>
    public static string QrText(string? shownAddress, int port)
    {
        var text = (shownAddress ?? string.Empty).Trim();

        if (text.Length == 0 || port is <= 0 or > 65535) return string.Empty;

        return "http://" + text + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// СЛОВА О ПРИЗНАКЕ «подсеть on-link». Отдельным методом, а не строкой в двух местах: правило
    /// выбора и выбор человека говорят об одном и том же, и разойдись они — человек прочитал бы
    /// два разных ответа об одном адресе.
    /// </summary>
    public static string OnLinkWords(bool onLink) =>
        Loc.T(onLink ? PanelStrings.PhoneAddressOnLink : PanelStrings.PhoneAddressNotOnLink);

    /// <summary>
    /// ГОДИТСЯ ЛИ АДРЕС ТЕЛЕФОНУ. Правило снято замером (<c>docs\PHONE-F0.md</c> §7.3) и звучит так:
    /// только **физический** адаптер, только **приватный** адрес и только когда эта подсеть
    /// **on-link** на нём самом.
    ///
    /// Метрика интерфейса не участвует НИКАК, и это не забывчивость: у туннеля WARP она ниже,
    /// чем у домашнего адаптера (5 против 25), поэтому «взять меньшую» выбрало бы туннель.
    /// </summary>
    public static bool Suitable(Address? address)
    {
        if (address is null) return false;
        if (address.Kind != AddressKind.Physical) return false;
        if (!address.OnLink) return false;

        return PrivateAddress(address.Ip);
    }

    /// <summary>
    /// Приватный ли адрес — и НЕ служебный. Годятся три полосы частных сетей (`10/8`, `172.16/12`,
    /// `192.168/16`); `169.254/16` (APIPA — «адреса нет») и `127/8` (петля) не годятся: телефон
    /// в них не попадёт никогда.
    /// </summary>
    public static bool PrivateAddress(string? address)
    {
        var parts = (address ?? string.Empty).Trim().Split('.');
        if (parts.Length != 4) return false;

        var numbers = new int[4];

        for (var index = 0; index < 4; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                return false;

            if (numbers[index] is < 0 or > 255) return false;
        }

        if (numbers[0] == 10) return true;

        // 172.16.0.0 … 172.31.255.255 — вторая полоса частных сетей.
        if (numbers[0] == 172 && numbers[1] is >= 16 and <= 31) return true;

        if (numbers[0] == 192 && numbers[1] == 168) return true;

        return false;
    }

    /// <summary>
    /// ОДИН АДРЕС, НЕСКОЛЬКО ИЛИ НИ ОДНОГО — без учёта выбора человека. Оставлено затем, чтобы
    /// у «просто отбора» было своё имя: правило отбора (<see cref="Suitable"/>) проверяется им,
    /// а выбор человека — <see cref="PickAddressChosen"/>.
    /// </summary>
    public static AddressPicked PickAddress(IReadOnlyList<Address>? addresses)
    {
        var picked = PickAddressChosen(addresses, string.Empty);

        return new AddressPicked(picked.Choice, picked.Candidates, picked.Chosen);
    }

    // ---- состояние в меню значка ------------------------------------------------------------

    /// <summary>
    /// ЕСТЬ ЛИ СТРОКА ТЕЛЕФОНА В МЕНЮ ЗНАЧКА ВООБЩЕ. Требование владельца дословно: пока телефон
    /// не разрешён, **строки нет вовсе** — не «выключено», а ничего.
    ///
    /// ⚠️ Довод ОДИН, и это тоже правило: строка про телефон не зависит от чужих функций.
    /// В первой редакции сюда затесался признак «разрешено ли обновление движка», и проверка
    /// поймала это сразу — решение, зависящее от постороннего признака, однажды погасило бы
    /// строку телефона из-за обновления.
    /// </summary>
    public static bool TrayLineShown(bool phoneAllowed) => phoneAllowed;

    /// <summary>
    /// ЧТО ИМЕННО ПОКАЗЫВАЕТ СТРОКА: «подключён» или «ждёт». Спрашивается ТОЛЬКО у той строки,
    /// которая есть (<see cref="TrayLineShown"/>); «не разрешён» здесь не выражается вовсе —
    /// если строки нет, то и состояния у неё нет.
    /// </summary>
    public static PhoneTrayState TrayState(bool connected) =>
        connected ? PhoneTrayState.Connected : PhoneTrayState.Waiting;

    /// <summary>
    /// ЧТО ИМЕННО НАПИСАНО В СТРОКЕ. Пусто — строки нет вовсе (см. <see cref="TrayLineShown"/>).
    /// Слова берутся из словаря, а не пишутся здесь: панель говорит на трёх языках.
    /// </summary>
    public static string TrayText(PhoneTrayState state) => state switch
    {
        PhoneTrayState.Connected => Loc.T(PanelStrings.PhoneTrayConnected),
        PhoneTrayState.Waiting => Loc.T(PanelStrings.PhoneTrayWaiting),
        _ => string.Empty,
    };

    /// <summary>
    /// СТРОКА ТЕЛЕФОНА ДЛЯ МЕНЮ ЗНАЧКА. Пустой текст — СТРОКИ НЕТ ВОВСЕ, а не «пустая строка»:
    /// пока человек не разрешил телефон, в меню о нём не сказано ничего (требование владельца
    /// 10.10.2026, и оно же записано в таблице решений).
    ///
    /// ⚠️ **Оба довода — ФАКТЫ, а не решения, и ни один из них не выводится здесь.** «Разрешён»
    /// приходит от настроек (<see cref="Settings.PanelSettings.PhoneEnabled"/>), «подключён» —
    /// от калитки, которая единственная знает, кто сопряжён и кого видели (движок об этом
    /// не говорит вовсе: замер <c>docs\PHONE-F0.md</c> §5.4). Строка лишь переводит их на язык
    /// меню, и второго места, где состояние телефона решается, быть не должно.
    ///
    /// Тон — всегда <see cref="TrayTone.Neutral"/>: телефон не «хорошо» и не «плохо», он либо
    /// ждёт, либо подключён, и красить это значило бы пугать человека цветом на ровном месте.
    /// </summary>
    public static TrayStatusLine TrayLine(bool allowed, bool connected) =>
        new(allowed ? TrayText(TrayState(connected)) : string.Empty, TrayTone.Neutral);

    // ---- слова, которые нужны и там, и тут --------------------------------------------------

    /// <summary>
    /// Как показать код сопряжения человеку: крупно, с объяснением и с названным сроком жизни.
    /// Отдельной строкой, а не «код: 123456»: код диктуют на телефон, и человеку важно понять,
    /// что делать дальше и сколько у него времени.
    /// </summary>
    public static string PairingCodeText(string code) =>
        Loc.T(PanelStrings.PhonePairingCodeFormat, code, (int)CodeLifetime.TotalMinutes);
}
