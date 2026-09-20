using System.Net;
using System.Runtime.InteropServices;

namespace DshTray;

/// <summary>
/// Кто слушает порт. Через GetExtendedTcpTable — без запуска netstat на каждый
/// тик таймера. Возвращает -1, если таблицу получить не удалось (тогда
/// вызывающий код уходит на запасной путь).
/// </summary>
internal static class TcpTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TableOwnerPidListener = 3;
    private const int StateListen = 2;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    public static int GetListenerPid(int port)
    {
        // IPv4: строка 24 байта, состояние на 0, порт на 8, PID на 20.
        var result = Scan(port, AfInet, 24, 0, 8, 20, out var failed);
        if (result > 0) return result;
        if (failed) return -1;

        // IPv6: строка 56 байт, состояние на 48, порт на 20, PID на 52.
        result = Scan(port, AfInet6, 56, 48, 20, 52, out failed);
        if (result > 0) return result;
        return failed ? -1 : 0;
    }

    private static int Scan(int port, int af, int rowSize, int stateOffset, int portOffset, int pidOffset, out bool failed)
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
                if (Marshal.ReadInt32(row, stateOffset) != StateListen) continue;
                var raw = Marshal.ReadInt32(row, portOffset);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF));
                if (localPort != port) continue;
                return Marshal.ReadInt32(row, pidOffset);
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
