using System;
using System.Linq;
using System.Reflection;
using DshPanel.Localization;
using DshPanel.Pricing;
using DshPanel.Server;
using DshPanel.Settings;
using DshPanel.Shell;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ЧИСТЫЕ РЕШЕНИЯ ТЕЛЕФОНА (этап F1): код сопряжения и окно его жизни, ограничение попыток,
/// белый список того, что телефону можно, выбор адреса для QR и строка состояния в меню значка.
///
/// **Почему всё это проверяется здесь, а не на живой калитке.** Калитки в этом куске нет вовсе —
/// и это не пробел, а порядок: сначала решения, которые можно перебрать без машины, потом дверь,
/// которая их исполняет. Поэтому здесь нет ни сокета, ни файла, ни таймера: каждое решение —
/// функция от данных, и каждое сочетание доводов можно назвать руками.
///
/// **Имена проверок называют то, что они ЛОВЯТ**, а не то, что они «проверяют»: у каждой
/// в комментарии сказано, какую правку она роняет.
/// </summary>
public class PhoneTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- код сопряжения

    /// <summary>
    /// КОД — РОВНО ШЕСТЬ ЦИФР, и это проверяется на СОТНЕ РАЗНЫХ кодов, а не на одном: одного
    /// мало, потому что «иногда шесть, иногда нет» выглядит зелёным ровно до первой беды
    /// у человека. Ловит правку «длина кода стала произвольной».
    /// </summary>
    [Fact]
    public void Код_сопряжения_ровно_шесть_цифр_и_всегда()
    {
        Assert.Equal(6, PhoneDecisions.CodeLength);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var code = PhoneDecisions.Code();

            Assert.Equal(6, code.Length);
            Assert.True(PhoneDecisions.CodeValid(code), $"код «{code}» не признан кодом");
        }
    }

    /// <summary>
    /// ВЕДУЩИЕ НУЛИ СОХРАНЯЮТСЯ. Ловит правку «код — это число»: число из «001234» стало бы
    /// «1234», и человек, вводящий шесть цифр с экрана, не понял бы, почему код не принимается.
    /// </summary>
    [Fact]
    public void Ведущий_ноль_не_пропадает()
    {
        var zeros = PhoneDecisions.Code(6, _ => 0);

        Assert.Equal("000000", zeros);
        Assert.Equal(6, zeros.Length);
        Assert.True(PhoneDecisions.CodeValid(zeros));

        // И код, начинающийся с нуля, сравнивается с собой как есть, а не как число.
        Assert.True(PhoneDecisions.CodeMatches("012345", "012345"));
        Assert.False(PhoneDecisions.CodeMatches("12345", "012345"));
    }

    /// <summary>
    /// ДОВОД-ДВЕРЬ ЦИФРЫ РАБОТАЕТ И ОТКАЗЫВАЕТ ГРОМКО: цифра вне 0…9 — исключение, а не «возьмём
    /// остаток». Ловит правку «зажмуриться на мусор»: тихо исправленная цифра — это код, которого
    /// никто не выпускал.
    /// </summary>
    [Fact]
    public void Довод_цифры_вне_нуля_девяти_роняет_громко()
    {
        Assert.Equal("777", PhoneDecisions.Code(3, _ => 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhoneDecisions.Code(3, _ => 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhoneDecisions.Code(3, _ => -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhoneDecisions.Code(0));
    }

    /// <summary>
    /// «ТОЛЬКО ЦИФРЫ И РОВНО ШЕСТЬ» — это ПРАВИЛО, а не пожелание: код из пяти знаков, из семи
    /// и с буквой отвергается. Ловит правку «принимаем что угодно» — иначе перебор кода
    /// начинается не с миллиона вариантов, а с чего попало.
    /// </summary>
    [Fact]
    public void Короткий_длинный_и_буквенный_код_не_годятся()
    {
        Assert.False(PhoneDecisions.CodeValid("12345"));
        Assert.False(PhoneDecisions.CodeValid("1234567"));
        Assert.False(PhoneDecisions.CodeValid("12345a"));
        Assert.False(PhoneDecisions.CodeValid("12 456"));
        Assert.False(PhoneDecisions.CodeValid(null));
        Assert.False(PhoneDecisions.CodeValid(string.Empty));

        Assert.True(PhoneDecisions.CodeValid("000000"));
        Assert.True(PhoneDecisions.CodeValid("999999"));
    }

    /// <summary>
    /// КОД ЖИВЁТ МИНУТЫ, А НЕ ВЕЧНО. Проверка идёт по ГРАНИЦЕ и по обе стороны от неё: «жив»
    /// на секунду раньше, «жив» ровно на границе и «мёртв» на секунду позже. Ловит правку
    /// «CodeAlive всегда истина» — иначе подсмотренный код годился бы и через сутки.
    /// </summary>
    [Fact]
    public void Код_живёт_минуты_и_умирает_на_границе()
    {
        var expiresAt = Now + PhoneDecisions.CodeLifetime;

        Assert.True(PhoneDecisions.CodeAlive(Now, expiresAt));
        Assert.True(PhoneDecisions.CodeAlive(expiresAt.AddSeconds(-1), expiresAt));
        Assert.True(PhoneDecisions.CodeAlive(expiresAt, expiresAt));
        Assert.False(PhoneDecisions.CodeAlive(expiresAt.AddSeconds(1), expiresAt));

        // И срок назван мерой, а не «где-то в коде»: он обязан быть коротким.
        Assert.Equal(TimeSpan.FromMinutes(5), PhoneDecisions.CodeLifetime);
        Assert.True(PhoneDecisions.CodeLifetime <= TimeSpan.FromMinutes(15), "код живёт недопустимо долго");
        Assert.True(
            PhoneDecisions.CodeAlive(Now, Now),
            "код, выпущенный «сейчас» с истёкшим сроком, обязан считаться живым только на границе");
    }

    /// <summary>
    /// СРАВНЕНИЕ КОДА НЕ «ЗВЕНИТ» ПО ДЛИНЕ И НЕ ПУСКАЕТ ПУСТОЕ. Здесь проверяется не время,
    /// а само правило: не тот код — нет; фрагмент кода — нет; пустой ожидаемый — нет ни при
    /// каком вводе. Ловит правку «сравниваем через ==» вместе с ослаблением «пустое совпадает с пустым».
    /// </summary>
    [Fact]
    public void Чужой_и_пустой_код_не_совпадают()
    {
        Assert.True(PhoneDecisions.CodeMatches("123456", "123456"));
        Assert.False(PhoneDecisions.CodeMatches("123456", "123457"));
        Assert.False(PhoneDecisions.CodeMatches("12345", "123456"));
        Assert.False(PhoneDecisions.CodeMatches("1234567", "123456"));
        Assert.False(PhoneDecisions.CodeMatches(string.Empty, "123456"));
        Assert.False(PhoneDecisions.CodeMatches(null, "123456"));

        // Ожидаемого нет — не совпадает НИЧЕГО, включая пустое: иначе «код не выпускали»
        // означало бы «входи, кто хочет».
        Assert.False(PhoneDecisions.CodeMatches(string.Empty, string.Empty));
        Assert.False(PhoneDecisions.CodeMatches("123456", string.Empty));
        Assert.False(PhoneDecisions.CodeMatches("123456", null));
    }

    // ---------------------------------------------------------------- ограничение попыток

    /// <summary>
    /// ПРЕДЕЛ ПОПЫТОК СПРАШИВАЕТСЯ: когда неудач с одного адреса набралось <see cref="PhoneDecisions.AddressLimit"/>,
    /// вход закрыт, и причина НАЗВАНА. Ловит правку «лимит не спрашивается вовсе» — при ней подбор
    /// шести цифр перестаёт упираться во что-либо.
    ///
    /// ⚠️ Пауза здесь ВЫКЛЮЧЕНА (<c>pause: TimeSpan.Zero</c>), и это не подгонка под ответ:
    /// при общих мерах пауза (<see cref="PhoneDecisions.BlockAfter"/> неудач подряд) срабатывает
    /// РАНЬШЕ предела на адрес, и проверка мерила бы паузу, называясь при этом пределом. Обе меры
    /// проверяются по отдельности — здесь предел, ниже пауза. ⚠️ Отключённая пауза НЕ ослабляет
    /// проверку: сама она стережётся соседней, а здесь важно, что счёт по адресу спрашивается.
    /// </summary>
    [Fact]
    public void Предел_попыток_с_адреса_отказывает()
    {
        var many = Enumerable
            .Range(0, PhoneDecisions.AddressLimit)
            .Select(index => new PhoneDecisions.Attempt("192.168.0.10", Now.AddSeconds(-6 * index)))
            .ToList();

        var refused = PhoneDecisions.DecideAttempt(many, "192.168.0.10", Now, pause: TimeSpan.Zero);

        Assert.False(refused.Allowed);
        Assert.Equal(PhoneDecisions.AttemptRefusal.AddressLimit, refused.Refusal);
        Assert.NotEqual(string.Empty, refused.Text());
        Assert.DoesNotContain("{", refused.Text(), StringComparison.Ordinal);

        // И ЧУЖОЙ адрес этим не задет: лимит считается по адресу, а не «вообще».
        Assert.True(PhoneDecisions.DecideAttempt(many, "192.168.0.11", Now, pause: TimeSpan.Zero).Allowed);

        // А на одну неудачу меньше — пускаем, и слов отказа нет.
        var allowed = PhoneDecisions.DecideAttempt(
            many.Take(PhoneDecisions.AddressLimit - 1).ToList(), "192.168.0.10", Now, pause: TimeSpan.Zero);

        Assert.True(allowed.Allowed);
        Assert.Equal(string.Empty, allowed.Text());
    }

    /// <summary>
    /// С ПЕРЕБОРЩИКОМ ПАУЗА, А НЕ ПРОСТО ОТКАЗ — и она называет, СКОЛЬКО ждать. Ловит правку
    /// «паузы нет»: без неё перебор идёт вплотную, а «отдохни» ничем не выражается. Пауза считается
    /// ОТ ПОСЛЕДНЕЙ неудачи и НЕЗАВИСИМО от окна счёта — поэтому проверяются и «только что»,
    /// и «пауза прошла», и случай, когда неудачи уже вышли из окна, а пауза всё ещё держит.
    /// </summary>
    [Fact]
    public void Перебор_с_адреса_включает_паузу_и_она_кончается()
    {
        var attempts = Enumerable
            .Range(0, PhoneDecisions.BlockAfter)
            .Select(index => new PhoneDecisions.Attempt("192.168.0.10", Now.AddSeconds(-index)))
            .ToList();

        var paused = PhoneDecisions.DecideAttempt(attempts, "192.168.0.10", Now);

        Assert.False(paused.Allowed);
        Assert.Equal(PhoneDecisions.AttemptRefusal.Paused, paused.Refusal);

        // Ждать — почти полные пять минут: последняя неудача была только что.
        Assert.True(paused.Wait > TimeSpan.FromMinutes(4), $"пауза названа неверно: {paused.Wait}");
        Assert.True(paused.Wait <= PhoneDecisions.AttemptPause);
        Assert.DoesNotContain("{", paused.Text(), StringComparison.Ordinal);

        // Пауза считается ОТ ПОСЛЕДНЕЙ НЕУДАЧИ и не зависит от окна счёта: сузь окно до секунды,
        // и неудачи из него выйдут — а пауза останется, потому что «отдохни» и «счёт за окно»
        // это разные меры.
        var narrow = PhoneDecisions.DecideAttempt(attempts, "192.168.0.10", Now, window: TimeSpan.FromSeconds(1));

        Assert.Equal(PhoneDecisions.AttemptRefusal.Paused, narrow.Refusal);

        // Пауза кончилась — пускаем снова. Считаем на длинном окне, чтобы это был именно «пауза
        // прошла», а не «неудачи ушли из окна».
        var later = Now + PhoneDecisions.AttemptPause + TimeSpan.FromSeconds(1);

        Assert.True(PhoneDecisions.DecideAttempt(attempts, "192.168.0.10", later).Allowed);
    }

    /// <summary>
    /// ОБЩИЙ ПРЕДЕЛ — ВТОРАЯ ЛИНИЯ, и она про случай, когда адреса МЕНЯЮТСЯ: по одному адресу
    /// неудач мало, а всего много. Ловит правку «считаем только по адресу»: тогда перебор
    /// с разных адресов не упирается ни во что.
    /// </summary>
    [Fact]
    public void Общий_предел_отказывает_при_разных_адресах()
    {
        var spread = Enumerable
            .Range(0, PhoneDecisions.TotalLimit)
            .Select(index => new PhoneDecisions.Attempt($"192.168.0.{index + 1}", Now.AddSeconds(-index)))
            .ToList();

        // По этому адресу — ни одной попытки, а вход всё равно закрыт.
        var verdict = PhoneDecisions.DecideAttempt(spread, "192.168.0.250", Now);

        Assert.False(verdict.Allowed);
        Assert.Equal(PhoneDecisions.AttemptRefusal.TotalLimit, verdict.Refusal);
        Assert.NotEqual(string.Empty, verdict.Text());
        Assert.DoesNotContain("{", verdict.Text(), StringComparison.Ordinal);
    }

    /// <summary>
    /// СЧИТАЮТСЯ ТОЛЬКО НЕУДАЧИ В ОКНЕ: старая неудача не держит дверь закрытой вечно, а «окно»
    /// задаётся доводом, и потому проверяется обеими сторонами. Ловит правку «считаем всё, что
    /// когда-либо было» — тогда один промах месяц назад закрывал бы вход навсегда.
    /// </summary>
    [Fact]
    public void Старые_неудачи_в_окно_не_попадают()
    {
        var old = Enumerable
            .Range(0, PhoneDecisions.AddressLimit * 2)
            .Select(index => new PhoneDecisions.Attempt("192.168.0.10", Now - PhoneDecisions.AttemptWindow - TimeSpan.FromMinutes(index + 1)))
            .ToList();

        Assert.Equal(0, PhoneDecisions.Count(old, "192.168.0.10", Now));
        Assert.True(PhoneDecisions.DecideAttempt(old, "192.168.0.10", Now).Allowed);

        // А те, что внутри окна, — считаются.
        var fresh = new[] { new PhoneDecisions.Attempt("192.168.0.10", Now - TimeSpan.FromMinutes(1)) };

        Assert.Equal(1, PhoneDecisions.Count(fresh, "192.168.0.10", Now));

        // Окно — довод: то же событие вне короткого окна не считается.
        Assert.Equal(0, PhoneDecisions.Count(fresh, "192.168.0.10", Now, TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// ПРОБЕЛ В АДРЕСЕ НЕ ОБХОДИТ ЛИМИТ. Пустой адрес и адрес из пробелов — ОДИН И ТОТ ЖЕ случай,
    /// названный словами. Ловит правку «сравниваем строки как пришли»: тогда « », «  » и «» были бы
    /// тремя разными ключами лимита, и он обходился бы пробелом.
    /// </summary>
    [Fact]
    public void Пустой_адрес_не_обходит_лимит_пробелом()
    {
        Assert.Equal(PhoneDecisions.UnknownAddress, PhoneDecisions.AddressOf(null));
        Assert.Equal(PhoneDecisions.UnknownAddress, PhoneDecisions.AddressOf(string.Empty));
        Assert.Equal(PhoneDecisions.UnknownAddress, PhoneDecisions.AddressOf("   "));

        var attempts = Enumerable
            .Range(0, PhoneDecisions.BlockAfter)
            .Select(index => new PhoneDecisions.Attempt(string.Empty, Now.AddSeconds(-index)))
            .ToList();

        // Адрес записан пробелами, а считается по-прежнему «неизвестным» — пауза включается.
        Assert.False(PhoneDecisions.DecideAttempt(attempts, "   ", Now).Allowed);
    }

    // ---------------------------------------------------------------- белый список

    /// <summary>
    /// ЗАКРЫТОЕ ПО УМОЛЧАНИЮ: ВСЁ, ЧЕГО НЕТ В СПИСКЕ, — ОТКАЗ СЛОВАМИ. Проверяются вызовы,
    /// которыми телефон НЕ должен распоряжаться: настройки движка, ключи, профиль, права,
    /// вложение файла, опись плагинов и «похожее» имя. Ловит правку «пускаем всё» и «пускаем
    /// по началу имени».
    /// </summary>
    [Fact]
    public void Всё_чего_нет_в_списке_получает_отказ_словами()
    {
        var forbidden = new[]
        {
            "settings/mutate",                  // настройки движка — не телефону их менять
            "settings/describe",                 // и читать их телефону незачем
            "credentials/set",                   // ключ модели телефону НЕ нужен (решение владельца)
            "credentials/describe",
            "workspace/delete",                  // чужие рабочие папки
            "directoryPicker/pick",              // выбор папки человеком на компьютере
            "directoryPicker/createDirectory",
            "fileUploads/upload",                // вложение с телефона — отдельное решение
            "llm/discoverModels",                // ходит в сеть
            "pluginInventory/list",              // опись установки — готовый отпечаток машины
            "session/rename",
            "session/listAll",                   // «похожее» имя не пускается по похожести

            // ⚠️ ТРИ ИМЕНИ ЛИНИИ 0.2.x, КОТОРЫХ В СПИСКЕ НЕТ, И ЭТО РЕШЕНИЕ (блоки D и E):
            // `subagents/list` — одно из 7 снятых с надобности имён (у 0.2.1 его нет, панель его
            // не зовёт и телефону давать нечего); `userQuestions/answer` и `attachWait` — новые
            // двери ответа, которые на живом блокирующем вопросе ОТВЕТ НЕ ПРИНИМАЮТ (замер `l1`:
            // `value:false` и молчание), а старая дверь `$events/result` работает на обеих линиях.
            // Появятся они в списке — это расширение поверхности телефона, то есть отдельное слово.
            "subagents/list",
            "userQuestions/answer",
            "userQuestions/attachWait",
            "session/",
            "$events/",
            "assets/app.js",                     // статика наружу не отдаётся вовсе
            "plugins/events",
        };

        foreach (var endpoint in forbidden)
        {
            var verdict = PhoneDecisions.DecideCall(endpoint);

            Assert.False(verdict.Allowed, $"«{endpoint}» пущен, а его нет в белом списке");
            Assert.NotEqual(string.Empty, verdict.Text());
            Assert.Contains(endpoint, verdict.Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("{", verdict.Text(), StringComparison.Ordinal);
        }

        // И вызов без имени — тоже отказ, а не «пустое имя значит поток событий».
        var nameless = PhoneDecisions.DecideCall("   ");

        Assert.False(nameless.Allowed);
        Assert.NotEqual(string.Empty, nameless.Text());
        Assert.DoesNotContain("{", nameless.Text(), StringComparison.Ordinal);
    }

    /// <summary>
    /// РАЗРЕШЕНО РОВНО ПЯТЬ ДЕЙСТВИЙ ТЕЛЕФОНА, и список назван целиком: тринадцать имён, из них
    /// одиннадцать — обычные вызовы движка и два — служебные имена событий. Ловит правку
    /// «в список попал лишний вызов»: молчаливое расширение списка — это ровно тот случай,
    /// ради которого список и закреплён.
    /// </summary>
    [Fact]
    public void Разрешено_ровно_пять_действий_и_ни_одним_больше()
    {
        var expected = new[]
        {
            "session/list", "session/page", "session/follow", "session/control",
            "session/create", "session/prompt", "session/cancel", "session/updateQueue",
            "session/modelCatalog", "directoryPicker/list", "agentPresets/list",
            PhoneDecisions.EventsEndpoint, PhoneDecisions.EventsResultEndpoint,
        };

        Assert.Equal(13, PhoneDecisions.Allowed.Count);
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            PhoneDecisions.Allowed.Select(call => call.Endpoint).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var endpoint in expected)
        {
            var verdict = PhoneDecisions.DecideCall(endpoint);

            Assert.True(verdict.Allowed, $"«{endpoint}» не пущен, а он в списке");
            Assert.Equal(string.Empty, verdict.Text());
        }

        // И «ответ на вопрос агента» на месте: без него пять действий стали бы четырьмя.
        Assert.Contains(PhoneDecisions.Allowed, call => call.Endpoint == "$events/result");
        Assert.Contains(PhoneDecisions.Allowed, call => call.Endpoint == "$events");
    }

    /// <summary>
    /// ФОРМА ДОВОДОВ НАЗВАНА В ОДНОМ МЕСТЕ: у `session/list` ключ `_request` (замер: без него
    /// движок отвечает «missing "_request"»), у прочих обычных вызовов — `request`, у служебных
    /// имён событий доводов нет вовсе. Ловит правку «все вызовы одинаковы»: у `session/list`
    /// с `request` перестал бы работать список разговоров — то есть первое из пяти действий.
    /// </summary>
    [Fact]
    public void Форма_доводов_названа_и_у_списка_сессий_она_своя()
    {
        Assert.Equal("_request", PhoneDecisions.ArgumentKeyFor("session/list"));
        Assert.Equal("request", PhoneDecisions.ArgumentKeyFor("session/page"));
        Assert.Equal("request", PhoneDecisions.ArgumentKeyFor("session/prompt"));
        Assert.Equal("request", PhoneDecisions.ArgumentKeyFor("agentPresets/list"));

        // Служебные имена событий доводов не несут: поток `$events` открывается пустыми доводами.
        Assert.Equal(string.Empty, PhoneDecisions.ArgumentKeyFor("$events"));
        Assert.Equal(string.Empty, PhoneDecisions.ArgumentKeyFor("$events/result"));

        // Незнакомое имя ключа не получает вовсе — доводы ему класть некуда.
        Assert.Equal(string.Empty, PhoneDecisions.ArgumentKeyFor("settings/mutate"));
        Assert.Equal(string.Empty, PhoneDecisions.ArgumentKeyFor(null));

        // И ключ доводов у одиночных вызовов — ОДИН И ТОТ ЖЕ, а не придуманный каждым.
        Assert.Single(
            PhoneDecisions.Allowed
                .Where(call => call.ArgumentKey == PhoneDecisions.ArgumentRequest)
                .Select(call => call.ArgumentKey)
                .Distinct(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- адрес для QR

    /// <summary>
    /// ГОДИТСЯ ТОЛЬКО ФИЗИЧЕСКИЙ, ТОЛЬКО ПРИВАТНЫЙ И ТОЛЬКО ON-LINK. Перебираются ВСЕ роды
    /// адаптеров из замера (<c>docs\PHONE-F0.md</c> §7.1) и все служебные полосы адресов.
    /// Ловит правку «берём любой Up-адаптер» — тогда в QR попал бы адрес туннеля или Hyper-V.
    /// </summary>
    [Fact]
    public void Адрес_годится_только_физический_приватный_и_on_link()
    {
        Assert.True(PhoneDecisions.Suitable(new Address("Ethernet", "192.168.0.10", AddressKind.Physical, true)));
        Assert.True(PhoneDecisions.Suitable(new Address("Wi-Fi", "10.1.2.3", AddressKind.Physical, true)));
        Assert.True(PhoneDecisions.Suitable(new Address("Ethernet", "172.20.5.5", AddressKind.Physical, true)));

        // Род: туннель и виртуальный адаптер не годятся, даже когда адрес приватный и on-link.
        Assert.False(PhoneDecisions.Suitable(new Address("CloudflareWARP", "172.16.0.5", AddressKind.Tunnel, true)));
        Assert.False(PhoneDecisions.Suitable(new Address("vEthernet (Default Switch)", "172.21.0.1", AddressKind.Virtual, true)));
        Assert.False(PhoneDecisions.Suitable(new Address("TAP-V9", "10.0.0.9", AddressKind.Virtual, true)));

        // Признак «подсеть on-link» обязателен: без него адрес может вести в туннель.
        Assert.False(PhoneDecisions.Suitable(new Address("Ethernet", "192.168.0.10", AddressKind.Physical, false)));

        // Служебные полосы: APIPA (адреса нет) и петля телефону не годятся.
        Assert.False(PhoneDecisions.Suitable(new Address("Wi-Fi", "169.254.1.1", AddressKind.Physical, true)));
        Assert.False(PhoneDecisions.Suitable(new Address("Ethernet", "127.0.0.1", AddressKind.Physical, true)));
        Assert.False(PhoneDecisions.Suitable(new Address("Ethernet", "8.8.8.8", AddressKind.Physical, true)));
        Assert.False(PhoneDecisions.Suitable(null));
    }

    /// <summary>
    /// ПОЛОСЫ ЧАСТНЫХ СЕТЕЙ — РОВНО ТРИ, и границы у них точные. Ловит правку «начинается
    /// с 172 — значит приватный»: `172.32.0.1` уже публичный, и панель показала бы телефону
    /// адрес, которого в его сети нет.
    /// </summary>
    [Fact]
    public void Частные_полосы_считаются_по_границам()
    {
        Assert.True(PhoneDecisions.PrivateAddress("10.0.0.0"));
        Assert.True(PhoneDecisions.PrivateAddress("10.255.255.255"));
        Assert.True(PhoneDecisions.PrivateAddress("172.16.0.0"));
        Assert.True(PhoneDecisions.PrivateAddress("172.31.255.255"));
        Assert.True(PhoneDecisions.PrivateAddress("192.168.0.0"));
        Assert.True(PhoneDecisions.PrivateAddress("192.168.255.255"));

        Assert.False(PhoneDecisions.PrivateAddress("172.15.255.255"));
        Assert.False(PhoneDecisions.PrivateAddress("172.32.0.0"));
        Assert.False(PhoneDecisions.PrivateAddress("11.0.0.1"));
        Assert.False(PhoneDecisions.PrivateAddress("192.169.0.1"));
        Assert.False(PhoneDecisions.PrivateAddress("169.254.0.1"));
        Assert.False(PhoneDecisions.PrivateAddress("127.0.0.1"));

        // Мусор — не адрес: часть не число, частей не четыре, число вне 0…255.
        Assert.False(PhoneDecisions.PrivateAddress("192.168.0.х"));
        Assert.False(PhoneDecisions.PrivateAddress("192.168.0"));
        Assert.False(PhoneDecisions.PrivateAddress("192.168.0.256"));
        Assert.False(PhoneDecisions.PrivateAddress("192.168.0.-1"));
        Assert.False(PhoneDecisions.PrivateAddress(null));
        Assert.False(PhoneDecisions.PrivateAddress(string.Empty));
    }

    /// <summary>
    /// ОДИН — ПОКАЗЫВАЕМ, НЕСКОЛЬКО — ПУСТЬ ВЫБЕРЕТ ЧЕЛОВЕК, НИ ОДНОГО — ГОВОРИМ СЛОВАМИ.
    /// Ловит правку «взять первый попавшийся»: она бы молча показала телефону адрес туннеля,
    /// если туннель в списке оказался раньше.
    /// </summary>
    [Fact]
    public void Один_несколько_и_ни_одного_адреса_решаются_по_разному()
    {
        var one = PhoneDecisions.PickAddress(new[]
        {
            new Address("CloudflareWARP", "172.16.0.5", AddressKind.Tunnel, true),
            new Address("Ethernet", "192.168.0.10", AddressKind.Physical, true),
        });

        Assert.Equal(AddressChoice.One, one.Choice);
        Assert.Single(one.Candidates);
        Assert.Equal("192.168.0.10", one.Chosen!.Ip);
        Assert.Contains("192.168.0.10", one.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", one.Text(), StringComparison.Ordinal);

        var several = PhoneDecisions.PickAddress(new[]
        {
            new Address("Ethernet", "192.168.0.10", AddressKind.Physical, true),
            new Address("Wi-Fi", "192.168.0.11", AddressKind.Physical, true),
        });

        Assert.Equal(AddressChoice.Several, several.Choice);
        Assert.Equal(2, several.Candidates.Count);
        Assert.Null(several.Chosen);
        Assert.Contains("2", several.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", several.Text(), StringComparison.Ordinal);

        var none = PhoneDecisions.PickAddress(new[]
        {
            new Address("CloudflareWARP", "172.16.0.5", AddressKind.Tunnel, true),
            new Address("vEthernet (Default Switch)", "172.21.0.1", AddressKind.Virtual, true),
            new Address("Беспроводная сеть", "169.254.1.1", AddressKind.Physical, false),
        });

        Assert.Equal(AddressChoice.None, none.Choice);
        Assert.Empty(none.Candidates);
        Assert.Null(none.Chosen);
        Assert.NotEqual(string.Empty, none.Text());
        Assert.DoesNotContain("{", none.Text(), StringComparison.Ordinal);

        // И пустой список — тот же ответ «показывать нечего», а не исключение.
        Assert.Equal(AddressChoice.None, PhoneDecisions.PickAddress(Array.Empty<Address>()).Choice);
        Assert.Equal(AddressChoice.None, PhoneDecisions.PickAddress(null).Choice);
    }

    /// <summary>
    /// МЕТРИКА ИНТЕРФЕЙСА НЕ УЧАСТВУЕТ В ВЫБОРЕ ВОВСЕ — и это проверяется по САМОМУ ТИПУ, а не
    /// по коду решения. Замер (<c>docs\PHONE-F0.md</c> §7.2): у туннеля WARP метрика 5, у домашнего
    /// Ethernet 25, то есть «взять меньшую» выбрало бы туннель. Если метрика когда-нибудь вернётся
    /// в <see cref="Address"/>, эта проверка упадёт и заставит перечитать замер.
    /// </summary>
    [Fact]
    public void Метрика_интерфейса_в_решение_не_входит()
    {
        var properties = typeof(Address)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(
            new[] { "Ip", "Kind", "Name", "OnLink" }.OrderBy(name => name, StringComparer.Ordinal),
            properties.OrderBy(name => name, StringComparer.Ordinal));

        Assert.DoesNotContain(properties, name => name.Contains("Metric", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- строка в меню значка

    /// <summary>
    /// НЕ РАЗРЕШЁН — СТРОКИ НЕТ ВООБЩЕ (требование владельца дословно): не «выключено», а пусто.
    /// Ловит правку «показываем состояние всегда»: тогда про телефон человек узнал бы из меню
    /// значка раньше, чем решил его заводить.
    ///
    /// ⚠️ Решение принимается по ОДНОМУ доводу — «разрешён ли телефон». Второго довода у строки
    /// нет намеренно: строка про телефон не имеет права зависеть от чужой функции (в первой
    /// редакции сюда затесался признак обновления движка, и проверка это поймала).
    /// </summary>
    [Fact]
    public void Не_разрешён_значит_строки_нет()
    {
        Assert.False(PhoneDecisions.TrayLineShown(phoneAllowed: false));
        Assert.True(PhoneDecisions.TrayLineShown(phoneAllowed: true));
    }

    /// <summary>
    /// ВКЛЮЧЁН И ЖДЁТ · ТЕЛЕФОН ПОДКЛЮЧЁН — ДВА РАЗНЫХ СЛОВА, и оба из словаря. Ловит правку
    /// «показываем одну и ту же фразу»: тогда «никто не подключён» и «телефон на связи» выглядели
    /// бы одинаково, и человек не понял бы, работает ли сопряжение.
    /// </summary>
    [Fact]
    public void Включён_ждёт_и_подключён_называются_по_разному()
    {
        Assert.Equal(PhoneTrayState.Waiting, PhoneDecisions.TrayState(connected: false));
        Assert.Equal(PhoneTrayState.Connected, PhoneDecisions.TrayState(connected: true));

        var waiting = PhoneDecisions.TrayText(PhoneTrayState.Waiting);
        var connected = PhoneDecisions.TrayText(PhoneTrayState.Connected);

        Assert.NotEqual(waiting, connected);
        Assert.Contains("Телефон", connected, StringComparison.Ordinal);

        // У «не разрешён» слов нет вовсе — строки не существует.
        Assert.Equal(string.Empty, PhoneDecisions.TrayText(PhoneTrayState.NotAllowed));

        // Оба слова есть на всех трёх языках и без шаблонных скобок.
        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in new[] { "PhoneTrayConnected", "PhoneTrayWaiting" })
            {
                var text = Loc.TIn(language, key);

                Assert.NotEqual(key, text);
                Assert.NotEqual(string.Empty, text);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }
        }
    }

    // ---------------------------------------------------------------- строки этого куска

    /// <summary>
    /// СТРОКИ ЭТОГО КУСКА ЕСТЬ НА ТРЁХ ЯЗЫКАХ И НЕ ОСТАВЛЯЮТ ШАБЛОННЫХ СКОБОК. Это не повтор
    /// проверки словарей: та сверяет ключи, а здесь проверяется, что у каждой строки, которую
    /// панель ЗОВЁТ, есть подстановки ровно там, где она их даёт, — иначе человек прочитает
    /// «{0}» вместо кода. Ловит правку «забыли перевод» и «забыли подстановку».
    /// </summary>
    [Fact]
    public void Строки_телефона_готовы_на_трёх_языках()
    {
        var plain = new[]
        {
            "PhoneTrayConnected", "PhoneTrayWaiting",
            "PhoneCallNoName", "PhoneCallOutsideList",
            "PhoneTooManyFromAddress", "PhoneTooManyTotal",
            "PhoneAddressNone", "PhoneAddressOnLink", "PhoneAddressNotOnLink",
        };

        var formatted = new[]
        {
            "PhonePairingCodeFormat", "PhoneCallRefusedFormat", "PhoneTryLaterFormat",
            "PhoneAddressOneFormat", "PhoneAddressManyFormat",
        };

        foreach (var language in new[] { "ru", "en", "zh" })
        {
            foreach (var key in plain)
            {
                var text = Loc.TIn(language, key);

                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }

            foreach (var key in formatted)
            {
                var text = Loc.TIn(language, key, 1, 2);

                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            }
        }

        // И собранные строки действительно называют то, что должны.
        var pairing = PhoneDecisions.PairingCodeText("123456");

        Assert.Contains("123456", pairing, StringComparison.Ordinal);
        Assert.Contains("5", pairing, StringComparison.Ordinal);
        Assert.DoesNotContain("{", pairing, StringComparison.Ordinal);
    }

    /// <summary>
    /// ПОРТ КАЛИТКИ — ЧИСЛО ИЗ ГРАНИЦ, И УМОЛЧАНИЕ ОДНО. Мусор в файле настроек (0, минус,
    /// 70000) приводится к умолчанию, а не остаётся «как написали». Ловит правку «порт вне границ
    /// не приводится к умолчанию»: тогда калитка пробовала бы занять порт 0 или 70000 и молча
    /// не открывалась бы.
    /// </summary>
    [Fact]
    public void Порт_вне_границ_становится_умолчанием()
    {
        Assert.Equal(43127, PhonePass.DefaultPort);

        Assert.Equal(43127, PanelSettings.NormalizePhonePort(0));
        Assert.Equal(43127, PanelSettings.NormalizePhonePort(-1));
        Assert.Equal(43127, PanelSettings.NormalizePhonePort(65536));
        Assert.Equal(43127, PanelSettings.NormalizePhonePort(int.MaxValue));

        // А настоящий порт человеку не подменяем — включая порт его сервера: это ЕГО файл.
        Assert.Equal(1, PanelSettings.NormalizePhonePort(1));
        Assert.Equal(65535, PanelSettings.NormalizePhonePort(65535));
        Assert.Equal(3080, PanelSettings.NormalizePhonePort(3080));

        // Умолчание настроек — то же число, что у двери: второго умолчания в панели нет.
        Assert.Equal(PhonePass.DefaultPort, PanelSettings.Default.PhonePort);
        Assert.False(PanelSettings.Default.PhoneEnabled);
        Assert.False(PanelSettings.Default.PhonePortChosen);
    }

    /// <summary>
    /// ДВЕРЬ НАСТРОЕК НЕ ТЕРЯЕТ ТЕЛЕФОН: порт приводится к границам, отметки времени — к круговому
    /// виду, неразобранная отметка становится ПУСТОЙ («не было»), а строки обрезаются. Ловит правку
    /// «поле телефона стёрли из Clean»: тогда первое же «Сохранить» в любом разделе настроек
    /// обнуляло бы разрешение на телефон и запомненный адрес.
    /// </summary>
    [Fact]
    public void Дверь_настроек_не_теряет_телефон()
    {
        var stamp = PricingDecisions.Stamp(Now);

        var clean = SettingsStore.Clean(new PanelSettings
        {
            PhoneEnabled = true,
            PhonePort = 70000,
            PhonePortChosen = true,
            PhoneLastAddress = "  192.168.0.10  ",
            PhoneLastSeen = stamp,
            PhoneConsentAt = stamp,
        });

        Assert.True(clean.PhoneEnabled);
        Assert.Equal(43127, clean.PhonePort);
        Assert.True(clean.PhonePortChosen);
        Assert.Equal("192.168.0.10", clean.PhoneLastAddress);
        Assert.Equal(stamp, clean.PhoneLastSeen);
        Assert.Equal(stamp, clean.PhoneConsentAt);

        // Неразобранное время — «не было», а не «было только что»: панель покажет правду.
        var broken = SettingsStore.Clean(new PanelSettings
        {
            PhoneLastSeen = "мусор",
            PhoneConsentAt = string.Empty,
        });

        Assert.Equal(string.Empty, broken.PhoneLastSeen);
        Assert.Equal(string.Empty, broken.PhoneConsentAt);

        // И настоящий порт человеку не подменяем.
        Assert.Equal(43128, SettingsStore.Clean(new PanelSettings { PhonePort = 43128 }).PhonePort);
    }
}
