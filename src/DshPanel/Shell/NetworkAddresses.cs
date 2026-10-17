using DshPanel.Server;

namespace DshPanel.Shell;

/// <summary>
/// РОД АДАПТЕРА ПО ЕГО ИМЕНИ И ОПИСАНИЮ — то, что решается ЧТЕНИЕМ ТЕКСТА, без единого обращения
/// к системе. Отдельное перечисление, а не <see cref="AddressKind"/>: у адаптера род свой
/// («физический», «туннель», «виртуальный», «петля», «неизвестный»), а у адреса — тот, что годится
/// телефону. Смешав их, панель однажды назвала бы петлю «физической».
/// </summary>
public enum AdapterKind
{
    /// <summary>Ethernet или Wi-Fi — настоящая сеть человека.</summary>
    Physical,

    /// <summary>Туннель (WARP, WireGuard, Tailscale, OpenVPN): адрес есть, а сети своей нет.</summary>
    Tunnel,

    /// <summary>Виртуальный адаптер (Hyper-V, Wi-Fi Direct, Bluetooth, WSL, TAP).</summary>
    Virtual,

    /// <summary>Петля.</summary>
    Loopback,

    /// <summary>Ни имя, ни описание не сказали ничего. Считается НЕГОДНЫМ: гадать нельзя.</summary>
    Unknown,
}

/// <summary>
/// СЫРЫЕ ДАННЫЕ ОБ ОДНОМ АДРЕСЕ МАШИНЫ, какими их отдаёт система: имя адаптера, его описание,
/// состояние, адрес, маска, метрика и признак «подсеть on-link».
///
/// ⚠️ **Метрика здесь ЕСТЬ, а в <see cref="Address"/> её нет — и это осознанно.** Собрать запись
/// без метрики, не прочитав её, нельзя: «не опираться на метрику» доказывается тем, что она
/// ПРИХОДИТ и всё равно не участвует в решении (`docs\PHONE-F0.md` §7.2: у туннеля WARP метрика
/// **5**, у домашнего Ethernet — **25**, и правило «взять меньшую» выбрало бы туннель).
/// Проверка подкладывает туннель с метрикой ниже и требует, чтобы он не попал в кандидаты.
/// </summary>
public sealed record RawAddress(
    string Name,
    string Description,
    bool Up,
    string Ip,
    string Mask,
    int Metric,
    bool OnLink);

/// <summary>
/// РАСКЛАДКА СЫРЫХ ДАННЫХ СИСТЕМЫ В АДРЕС ПАНЕЛИ — ЧИСТАЯ ФУНКЦИЯ, и здесь нет ни системы,
/// ни сети, ни слушателя.
///
/// **Зачем отдельным файлом.** Правило выбора адреса для телефона снято замером
/// (<c>docs\PHONE-F0.md</c> §7.3) и живёт в <see cref="PhoneDecisions.Suitable"/>: только
/// физический адаптер, только приватный адрес и только когда подсеть **on-link**. Всё остальное —
/// ПЕРЕВОД того, что говорит система, на язык этого правила: «CloudflareWARP» → туннель,
/// «vEthernet (Default Switch)» → виртуальный, «Realtek PCIe GbE Family Controller» → физический.
///
/// Перевод чистый намеренно: списки имён взяты у замера (<c>docs\PHONE-F0.md</c> §7.1), и проверка
/// перебирает их без машины. Реализация двери системы (`System.Net.NetworkInformation`) живёт
/// РЯДОМ и по устройству типов не может быть позвана из проверок — см.
/// <see cref="RealNetworkAddresses"/>.
///
/// ⚠️ **Метрика не участвует НИГДЕ.** Она приходит в <see cref="RawAddress"/>, чтобы её можно было
/// подложить и убедиться, что решение от неё не зависит, — и не переходит в <see cref="Address"/>
/// вовсе.
/// </summary>
public static class NetworkAddresses
{
    /// <summary>
    /// Чего не бывает в описании физического адаптера. Список — ДАННЫЕ, а не «похоже на правду»:
    /// он снят с машины владельца (<c>docs\PHONE-F0.md</c> §7.1: CloudflareWARP · vEthernet
    /// (Default Switch) · Bluetooth PAN · два Wi-Fi Direct · OpenVPN DCO · TAP-V9 · Loopback).
    /// </summary>
    public static readonly string[] NotPhysical = new[]
    {
        "Tunnel", "WARP", "WireGuard", "Tailscale", "OpenVPN", "TAP",
        "Hyper-V", "vEthernet", "Wi-Fi Direct", "Bluetooth", "Loopback", "WSL",
    };

    /// <summary>Что в имени или описании значит «туннель наружу».</summary>
    private static readonly string[] TunnelWords = new[]
    {
        "Tunnel", "WARP", "WireGuard", "Tailscale", "OpenVPN", "TAP",
    };

    /// <summary>Что значит «виртуальный адаптер» — не своя сеть человека.</summary>
    private static readonly string[] VirtualWords = new[]
    {
        "Hyper-V", "vEthernet", "Wi-Fi Direct", "Bluetooth", "WSL",
    };

    /// <summary>
    /// РОД АДАПТЕРА ПО ТЕКСТУ. Ищем и в имени, и в описании: у туннеля WARP имя — «CloudflareWARP»,
    /// а у Hyper-V имя — «vEthernet (Default Switch)», то есть примета бывает и там и тут.
    ///
    /// Порядок проверок важен и назван: **петля → туннель → виртуальный → физический**. Туннель
    /// раньше виртуального потому, что OpenVPN DCO — это TAP по устройству, а сеть у него чужая;
    /// назвать его «виртуальным» значило бы сказать про него правду, но не ту, которая решает.
    ///
    /// ⚠️ **Ничего не узнали — <see cref="AdapterKind.Unknown"/>, а не «физический».** Умолчание
    /// «физический» пропустило бы к телефону адрес неизвестного адаптера, то есть ровно то гадание,
    /// от которого замер и предостерегает.
    /// </summary>
    public static AdapterKind KindOf(string? name, string? description)
    {
        var text = ((name ?? string.Empty) + " " + (description ?? string.Empty)).Trim();
        if (text.Length == 0) return AdapterKind.Unknown;

        if (Contains(text, "loopback") || Contains(text, "петля")) return AdapterKind.Loopback;

        foreach (var word in TunnelWords)
        {
            if (Contains(text, word)) return AdapterKind.Tunnel;
        }

        foreach (var word in VirtualWords)
        {
            if (Contains(text, word)) return AdapterKind.Virtual;
        }

        // ФИЗИЧЕСКИЙ — по признаку, а не «всё, что не подошло»: иначе неизвестный адаптер
        // («Сетевое подключение 2», «Ethernet 3» на чужой машине) стал бы кандидатом в телефон.
        // Приметы взяты у замера: «Ethernet» (Realtek PCIe GbE) и «Беспроводная сеть» /
        // «Wireless» / «Wi-Fi» (Realtek USB Wi-Fi).
        foreach (var word in new[] { "ethernet", "беспроводн", "wireless", "wi-fi", "wifi", "802.11" })
        {
            if (Contains(text, word)) return AdapterKind.Physical;
        }

        return AdapterKind.Unknown;
    }

    /// <summary>
    /// РОД АДРЕСА ТЕЛЕФОНА по роду адаптера — и на этом всё: туннель, виртуальный, петля и
    /// неизвестный к телефону не годятся, потому что в них он не попадёт.
    /// </summary>
    public static AddressKind KindFor(AdapterKind adapter) => adapter switch
    {
        AdapterKind.Physical => AddressKind.Physical,
        AdapterKind.Tunnel => AddressKind.Tunnel,
        _ => AddressKind.Virtual,
    };

    /// <summary>
    /// РАСКЛАДКА ОДНОЙ ЗАПИСИ СИСТЕМЫ В АДРЕС ПАНЕЛИ. <c>null</c> — запись телефону не годится
    /// и в списке кандидатов появляться не должна.
    ///
    /// Что отсеивается ЗДЕСЬ и почему именно здесь: адаптер **не поднят** (в замере так выглядела
    /// беспроводная сеть с адресом `169.254.х.х` — «адрес есть, сети нет»), адаптер **не
    /// физический** (туннель WARP поднят и адрес у него настоящий — `172.16.0.х/32`), и адрес
    /// **пуст**.
    ///
    /// ⚠️ Приватность адреса и признак on-link здесь НЕ проверяются: это делает
    /// <see cref="PhoneDecisions.Suitable"/>, и второй такой проверки в панели быть не должно —
    /// разойдясь, они пустили бы к телефону адрес, который одна из них считает негодным.
    /// </summary>
    public static Address? ToAddress(RawAddress? raw)
    {
        if (raw is null) return null;
        if (!raw.Up) return null;
        if (string.IsNullOrWhiteSpace(raw.Ip)) return null;

        var adapter = KindOf(raw.Name, raw.Description);
        if (adapter != AdapterKind.Physical) return null;

        var mask = (raw.Mask ?? string.Empty).Trim();

        // Признак on-link приходит от системы (см. RealNetworkAddresses). Нет маски — сказать
        // «подсеть ведёт на этот адаптер» нечем, и ответ «да» был бы выдумкой.
        var onLink = raw.OnLink && mask.Length > 0;

        return new Address(raw.Name ?? string.Empty, raw.Ip.Trim(), KindFor(adapter), onLink);
    }

    /// <summary>
    /// АДРЕСА, КОТОРЫЕ ПАНЕЛЬ ВООБЩЕ РАССМАТРИВАЕТ: те, что прошли <see cref="ToAddress"/>.
    /// Порядок записей СОХРАНЯЕТСЯ — он приходит от системы, а переставлять его значит показывать
    /// человеку другой список при том же составе (то же правило, что у
    /// <see cref="PhoneDecisions.PickAddress"/>).
    ///
    /// ⚠️ **Пусто — нормальный ответ, а не сбой.** Замер §7.1: подходящий адрес на машине ровно
    /// один, и это редкость; на машине без своей сети подходящих нет вовсе, и панель обязана
    /// сказать об этом словами (<see cref="PhoneDecisions.PickAddress"/> → <c>None</c>).
    /// </summary>
    public static IReadOnlyList<Address> Collect(IEnumerable<RawAddress>? raw)
    {
        var list = new List<Address>();

        foreach (var item in raw ?? Enumerable.Empty<RawAddress>())
        {
            var address = ToAddress(item);
            if (address is not null) list.Add(address);
        }

        return list;
    }

    /// <summary>
    /// ЛЕЖИТ ЛИ ХОТЬ ОДИН ШЛЮЗ В ПОДСЕТИ ЭТОГО АДРЕСА — то самое «подсеть ведёт на этот адаптер
    /// напрямую» (правило §7.3, п. 4). Считается ЧИСТО, по данным: у туннеля WARP свой шлюз
    /// и своя подсеть `/32`, и адрес домашней сети в неё не попадает.
    ///
    /// ⚠️ **Чего признак не видит** (названо и в <see cref="RealNetworkAddresses"/>): в сети может
    /// не быть шлюза вовсе (мост, изолированный сегмент) — тогда годный адрес останется непригодным;
    /// и наоборот, шлюз есть, а сеть недоступна (изоляция клиентов на роутере). Оба случая решает
    /// живая проба с телефона, и обещать больше панель не имеет права.
    /// </summary>
    public static bool GatewayInside(string? address, string? mask, IEnumerable<string>? gateways)
    {
        if (!TryNumber(address, out var mine)) return false;
        if (!TryNumber(mask, out var netmask)) return false;

        // Маска обязана быть настоящей (единицы подряд): «255.0.255.0» дала бы подсеть,
        // которой не бывает, и признак стал бы случайным.
        if (!Continuous(netmask)) return false;

        foreach (var gateway in gateways ?? Enumerable.Empty<string>())
        {
            if (!TryNumber(gateway, out var hop)) continue;

            // ШИРОКОВЕЩАТЕЛЬНЫЙ адрес ЭТОЙ подсети и сама машина шлюзом не считаются: первый —
            // не узел, второй — мы сами, и оба дали бы «on-link» там, где в сети никого нет.
            if (hop == ((mine & netmask) | ~netmask)) continue;
            if (hop == mine) continue;

            if ((hop & netmask) != (mine & netmask)) continue;

            return true;
        }

        return false;
    }

    /// <summary>Адрес как число. Не разобрался (не четыре числа) — «нет».</summary>
    private static bool TryNumber(string? address, out uint number)
    {
        number = 0;

        var parts = (address ?? string.Empty).Trim().Split('.');
        if (parts.Length != 4) return false;

        var value = 0u;

        foreach (var part in parts)
        {
            if (!byte.TryParse(part, out var octet)) return false;

            value = (value << 8) | octet;
        }

        number = value;

        return true;
    }

    /// <summary>Единицы в маске идут подряд, а не вперемешку.</summary>
    private static bool Continuous(uint mask)
    {
        var zerosSeen = false;

        for (var bit = 31; bit >= 0; bit--)
        {
            var set = (mask & (1u << bit)) != 0;

            if (set && zerosSeen) return false;
            if (!set) zerosSeen = true;
        }

        return true;
    }

    private static bool Contains(string text, string word) =>
        text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
