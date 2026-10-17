using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DshPanel.Shell;

/// <summary>
/// НАСТОЯЩАЯ ДВЕРЬ К АДАПТЕРАМ МАШИНЫ. Единственное место, где панель спрашивает у системы
/// «какие у меня адреса и какие из них ведут в сеть напрямую».
///
/// **Почему отдельным файлом.** Раскладка сырых данных в адрес панели — чистая функция
/// (<see cref="NetworkAddresses"/>), и она проверяется перебором без машины. Здесь — то, что
/// проверкой не перебрать: <see cref="NetworkInterface"/>, маски и подсети. Разделение ровно то же,
/// что у калитки (`PhoneGate` и `PhoneGateRealDoors`): решение — отдельно, «как оно устроено
/// на самом деле» — отдельно.
///
/// **Признак on-link берётся у САМОЙ СЕТИ, и вот почему не из таблицы маршрутов.** Первая
/// редакция читала таблицу маршрутов через <c>GetIpForwardTable2</c>. Раскладку
/// <c>MIB_IPFORWARD_ROW2</c> в .NET воспроизвести не удалось: структура приходила разобранной
/// не по тем смещениям (шаг строки 104, а поля — со сдвигом), и признак получался ВСЕГДА ложным.
/// Ошибку нашёл КАДР раздела: на нём стояло «телефону показывать нечего», хотя домашняя сеть
/// у машины есть. Вместо пляски с байтами взят примитив BCL, у которого нет раскладки вовсе:
/// **шлюз по умолчанию лежит в подсети этого адреса** — то есть в сети, куда пакеты уходят
/// напрямую, есть кто-то кроме машины.
///
/// ⚠️ **Чего этот признак не видит, названо прямо.** Шлюза в сети может не быть (изолированный
/// сегмент, «роутер в режиме моста»), и тогда годный адрес останется непригодным; и наоборот,
/// шлюз стоит в подсети, а сеть всё равно недоступна (изоляция клиентов на роутере). Оба случая
/// разрешает одно — **живая проба с телефона** (<c>docs\PHONE-F0.md</c> §7.3, п. 6): адрес
/// считается подтверждённым только тогда, когда с телефона открылась страница. Панель об этом
/// говорит словами в разделе, а не притворяется, что знает наверняка.
///
/// ⚠️ **В ПРОВЕРКАХ ЭТОТ КОД НЕ ВЫПОЛНЯЕТСЯ, и это устроено, а не обещано.** Проверке нужна
/// дверь-поставщик адресов; но мало ли — поэтому здесь стоит <see cref="Test"/>: непустое
/// значение отдаётся ВМЕСТО системы, и ни одна строка <see cref="NetworkInterface"/> не
/// выполняется. Продукт сюда не пишет ни при каком запуске.
/// </summary>
public static class RealNetworkAddresses
{
    /// <summary>
    /// ПОДСТАВНЫЕ АДРЕСА ДЛЯ ПРОВЕРОК. <c>null</c> — «спрашивай систему», и это состояние продукта.
    /// Ставится ТОЛЬКО проверками.
    ///
    /// ⚠️ **Зачем это здесь, если дверь и так приходит доводом.** Раздел настроек собирается
    /// в проверке тем же путём, что в жизни (<c>SettingsWindow.Attach</c>), и «подставить дверь»
    /// означало бы пройти ДРУГОЙ путь: проверенное правило держалось бы на стенде, а не на
    /// продукте. Здесь дверь настоящая, а система подменена её же значением.
    /// </summary>
    public static IReadOnlyList<RawAddress>? Test { get; set; }

    /// <summary>
    /// СОБРАТЬ АДРЕСА МАШИНЫ. Пусто — ответ, а не сбой: подходящего адреса у машины может
    /// не быть вовсе, и панель скажет об этом словами.
    ///
    /// Берутся только адреса IPv4: телефон в своей сети ходит по IPv4, а `fe80::…` ему не годится.
    /// Род адаптера и пригодность адреса здесь НЕ решаются — их решает правило
    /// (<see cref="NetworkAddresses.Collect"/> и <see cref="Server.PhoneDecisions.Suitable"/>), —
    /// чтобы у отказа было одно место.
    /// </summary>
    public static IReadOnlyList<RawAddress> Read()
    {
        var test = Test;
        if (test is not null) return test;

        var raw = new List<RawAddress>();

        NetworkInterface[] adapters;

        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            // Спросить не удалось — «адресов нет»: это честнее выдуманного списка.
            return raw;
        }

        foreach (var adapter in adapters)
        {
            var up = adapter.OperationalStatus == OperationalStatus.Up;

            IPInterfaceProperties properties;

            try
            {
                properties = adapter.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var index = IndexOf(properties);
            var gateways = Gateways(properties);

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var mask = unicast.IPv4Mask is null ? string.Empty : unicast.IPv4Mask.ToString();
                var address = unicast.Address.ToString();

                raw.Add(new RawAddress(
                    adapter.Name,
                    adapter.Description,
                    up,
                    address,
                    mask,
                    index,
                    NetworkAddresses.GatewayInside(address, mask, gateways)));
            }
        }

        return raw;
    }

    /// <summary>
    /// ШЛЮЗЫ ПО УМОЛЧАНИЮ ЭТОГО АДАПТЕРА — только IPv4. Они и только они отвечают на вопрос
    /// «есть ли в этой подсети кто-то, кроме машины»: у туннеля WARP шлюз свой и лежит в его
    /// подсети `/32`, куда адрес домашней сети не попадает.
    ///
    /// Не прочитались — пустой список, и тогда on-link не подтверждается ни у одного адреса:
    /// непригодный адрес лучше выдуманного.
    /// </summary>
    private static IReadOnlyList<string> Gateways(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GatewayAddresses
                .Where(gateway => gateway?.Address?.AddressFamily == AddressFamily.InterNetwork)
                .Select(gateway => gateway.Address.ToString())
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// НОМЕР ИНТЕРФЕЙСА (IPv4) — простая примета адаптера. Метрика интерфейса лежит рядом и НЕ
    /// берётся: правило выбора адреса на неё не опирается (<c>docs\PHONE-F0.md</c> §7.2: у туннеля
    /// WARP она **5**, у домашнего Ethernet — **25**, и «взять меньшую» выбрало бы туннель).
    /// В решение о пригодности адреса номер не входит вовсе.
    ///
    /// Не прочитался — <c>0</c>, и это «неизвестно».
    /// </summary>
    private static int IndexOf(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
