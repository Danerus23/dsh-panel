using System.Net;
using System.Runtime.InteropServices;

namespace DshPanel.Server;

/// <summary>
/// Кто слушает порт. Через <c>GetExtendedTcpTable</c> — без запуска netstat на каждый тик.
///
/// Перенесено из v1 без изменения разбора таблицы: смещения проверены там на живых прогонах,
/// и переписывать работающее ради красоты здесь нечего. Отличия только в соглашениях об ошибках.
/// </summary>
public static class PortTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>Порт и номер процесса, который его слушает.</summary>
    public readonly record struct Listener(int Port, int Pid);

    /// <summary>
    /// ВСЕ порты, которые кто-то слушает, с номерами процессов. Нужен разведке: панель обязана
    /// находить сервер DSH, который подняла не она (решение владельца 24.09.2026), а для этого
    /// его порт надо сначала найти — спрашивать у человека «на каком порту ваш DSH» нельзя.
    ///
    /// Адрес НАМЕРЕННО не фильтруется. Соблазн оставить только loopback велик, но он вреден:
    /// сервер, слушающий <c>0.0.0.0</c>, на <c>127.0.0.1</c> всё равно отвечает, и решение
    /// принимает не адрес, а отпечаток. А разбор поля адреса — лишний байтовый сдвиг, в котором
    /// легко ошибиться молча.
    ///
    /// <c>null</c> — таблицу получить не удалось. Это НЕ то же самое, что «никто ничего не слушает»:
    /// разведка обязана сказать «не смогла посмотреть», а не «серверов нет».
    /// </summary>
    public static IReadOnlyList<Listener>? ListListeners()
    {
        var found = new List<Listener>();

        // IPv4 и IPv6 собираются в один список: один и тот же процесс нередко слушает оба
        // семейства на одном порту, и повтор ему в списке не нужен.
        var ok4 = Collect(found, AfInet, 24, 0, 8, 20, 16);
        var ok6 = Collect(found, AfInet6, 56, 48, 20, 52, 44);

        if (!ok4 && !ok6) return null;

        var seen = new HashSet<int>();
        var result = new List<Listener>();
        foreach (var listener in found)
        {
            if (seen.Add(listener.Port)) result.Add(listener);
        }

        return result;
    }

    /// <summary>
    /// ЭТА СТРОКА — СЛУШАТЕЛЬ? Чистое решение, чтобы его можно было проверить строками
    /// синтетическими, а не живой таблицей (DEF-074, §4.1).
    ///
    /// ⚠️ **`dwState` В РЕШЕНИИ НЕ УЧАСТВУЕТ, И ЭТО ГЛАВНОЕ.** В таблице
    /// <c>TCP_TABLE_OWNER_PID_LISTENER</c> лежат ТОЛЬКО слушатели — фильтр «состояние равно
    /// LISTEN» приехал сюда из v1, где он стоял на таблице СОЕДИНЕНИЙ, и здесь он выкидывает
    /// живых слушателей. Замер на машине владельца 30.10.2026: у порта 3080 (живой сервер DSH,
    /// pid 21008) в слушающей таблице <c>dwState = 6</c>, а не 2, — и панель 2.4.4 не находила
    /// слушателя вовсе, снимала свою же запись о сервере и упиралась в «порт занят» (DEF-074).
    /// Тот же замер показал состояния 0 и 1 у других настоящих слушателей: поле состояния
    /// в этой таблице не значит того, что мы у него спрашивали.
    ///
    /// Приметы, которые значат: локальный порт назван, удалённый порт нулевой (у слушателя
    /// соединения нет) и номер процесса ненулевой (иначе это не «кто слушает», а «дырка»).
    /// </summary>
    internal static bool RowIsListener(int state, int localPort, int remotePort, int pid)
    {
        // Намеренно не читается: довод оставлен в подписи, чтобы проверка могла подать строку
        // с `dwState = 6` и доказать, что он на решение не влияет.
        _ = state;

        return localPort > 0 && remotePort == 0 && pid > 0;
    }

    private static bool Collect(
        List<Listener> into, int af, int rowSize, int stateOffset, int portOffset, int pidOffset, int remotePortOffset)
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TableOwnerPidListener, 0);
        if (status != ErrorInsufficientBuffer && status != 0) return false;
        if (size <= 0) return true;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, af, TableOwnerPidListener, 0);
            if (status != 0) return false;

            var count = Marshal.ReadInt32(buffer);
            var row = IntPtr.Add(buffer, 4);
            for (var i = 0; i < count; i++, row = IntPtr.Add(row, rowSize))
            {
                var state = Marshal.ReadInt32(row, stateOffset);

                var raw = Marshal.ReadInt32(row, portOffset);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF));
                var rawRemote = Marshal.ReadInt32(row, remotePortOffset);
                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)(rawRemote & 0xFFFF));
                var owner = Marshal.ReadInt32(row, pidOffset);

                if (RowIsListener(state, localPort, remotePort, owner)) into.Add(new Listener(localPort, owner));
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Номер процесса, слушающего порт; <c>0</c> — никто не слушает, <c>-1</c> — таблицу получить
    /// не удалось. Различать эти два случая обязательно: «не смог узнать» нельзя показывать
    /// человеку как «порт свободен».
    /// </summary>
    public static int GetListenerPid(int port)
    {
        // IPv4: строка 24 байта, состояние на 0, порт на 8, PID на 20.
        var result = Scan(port, AfInet, 24, 0, 8, 20, 16, out var failed);
        if (result > 0) return result;
        if (failed) return -1;

        // IPv6: строка 56 байт, состояние на 48, порт на 20, PID на 52.
        result = Scan(port, AfInet6, 56, 48, 20, 52, 44, out failed);
        if (result > 0) return result;
        return failed ? -1 : 0;
    }

    private static int Scan(
        int port, int af, int rowSize, int stateOffset, int portOffset, int pidOffset, int remotePortOffset, out bool failed)
    {
        failed = false;
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TableOwnerPidListener, 0);
        if (status != ErrorInsufficientBuffer && status != 0) { failed = true; return 0; }
        if (size <= 0) return 0;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, af, TableOwnerPidListener, 0);
            if (status != 0) { failed = true; return 0; }

            var count = Marshal.ReadInt32(buffer);
            var row = IntPtr.Add(buffer, 4);
            for (var i = 0; i < count; i++, row = IntPtr.Add(row, rowSize))
            {
                var state = Marshal.ReadInt32(row, stateOffset);
                var raw = Marshal.ReadInt32(row, portOffset);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF));
                if (localPort != port) continue;

                var rawRemote = Marshal.ReadInt32(row, remotePortOffset);
                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)(rawRemote & 0xFFFF));
                var owner = Marshal.ReadInt32(row, pidOffset);

                if (!RowIsListener(state, localPort, remotePort, owner)) continue;

                return owner;
            }
        }
        catch
        {
            failed = true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return 0;
    }
}
