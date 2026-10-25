using System.Net;
using System.Runtime.InteropServices;

namespace SpeedLimit;

/// <summary>Eine TCP-Verbindung eines Programms.</summary>
public readonly record struct Connection(IPAddress Remote, int RemotePort, string State);

/// <summary>Zuordnung (Protokoll, lokaler Port) → Programmname über die TCP/UDP-Tabellen von Windows, plus offene TCP-Verbindungen je Programm.</summary>
public sealed class PortMap
{
    delegate uint TableFn(IntPtr table, ref int size);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1, MIB_TCP_STATE_LISTEN = 2;

    volatile Dictionary<(bool Tcp, ushort Port), string> map = [];
    volatile Dictionary<string, List<Connection>> connections = [];
    volatile IReadOnlyDictionary<int, string> names = new Dictionary<int, string>();

    public string? Lookup(bool tcp, ushort port) => map.GetValueOrDefault((tcp, port));

    /// <summary>Programmname zu einer Prozess-ID; für ganz neue Prozesse direkt nachgeschlagen.</summary>
    public string? NameOf(int pid)
    {
        if (names.TryGetValue(pid, out var name)) return name;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null; // Prozess schon beendet
        }
    }

    public IReadOnlyList<Connection> ConnectionsOf(string name) => connections.GetValueOrDefault(name) ?? [];

    // shortcut: wird 1x pro Sekunde aufgefrischt – ganz neue Verbindungen sind bis dahin ungedrosselt
    public void Refresh(IReadOnlyDictionary<int, string> namesByPid)
    {
        var m = new Dictionary<(bool, ushort), string>();
        var conns = new Dictionary<string, List<Connection>>();
        void AddTcp(string name, IntPtr row, int stateOff, IPAddress remote, int remotePortOff)
        {
            var state = Marshal.ReadInt32(row + stateOff);
            if (state == MIB_TCP_STATE_LISTEN || remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any)) return;
            if (!conns.TryGetValue(name, out var list)) conns[name] = list = [];
            list.Add(new(remote, Port(row + remotePortOff), StateText(state)));
        }

        // Zeilenlayout (Bytes): MIB_TCPROW_OWNER_PID, MIB_TCP6ROW_OWNER_PID, MIB_UDPROW_OWNER_PID, MIB_UDP6ROW_OWNER_PID
        Read(m, namesByPid, true, (IntPtr t, ref int s) => GetExtendedTcpTable(t, ref s, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0), 24, 8, 20,
            (name, row) => AddTcp(name, row, 0, new IPAddress((uint)Marshal.ReadInt32(row + 12)), 16));
        Read(m, namesByPid, true, (IntPtr t, ref int s) => GetExtendedTcpTable(t, ref s, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0), 56, 20, 52,
            (name, row) =>
            {
                var addr = new byte[16];
                Marshal.Copy(row + 24, addr, 0, 16);
                AddTcp(name, row, 48, new IPAddress(addr), 44);
            });
        Read(m, namesByPid, false, (IntPtr t, ref int s) => GetExtendedUdpTable(t, ref s, false, AF_INET, UDP_TABLE_OWNER_PID, 0), 12, 4, 8);
        Read(m, namesByPid, false, (IntPtr t, ref int s) => GetExtendedUdpTable(t, ref s, false, AF_INET6, UDP_TABLE_OWNER_PID, 0), 28, 20, 24);
        map = m;
        connections = conns;
        names = namesByPid;
    }

    static string StateText(int state) => state switch
    {
        5 => "verbunden",
        3 or 4 => "verbindet…",
        _ => "wird geschlossen",
    };

    static ushort Port(IntPtr p)
    {
        var v = (uint)Marshal.ReadInt32(p);
        return (ushort)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF)); // Netzwerk-Byte-Order
    }

    static void Read(Dictionary<(bool, ushort), string> m, IReadOnlyDictionary<int, string> names, bool tcp, TableFn get,
                     int rowSize, int portOff, int pidOff, Action<string, IntPtr>? onRow = null)
    {
        var size = 0;
        var buf = IntPtr.Zero;
        try
        {
            uint err;
            while ((err = get(buf, ref size)) == 122) // ERROR_INSUFFICIENT_BUFFER
            {
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
            }
            if (err != 0) return;
            var count = Marshal.ReadInt32(buf);
            for (var i = 0; i < count; i++)
            {
                var row = buf + 4 + i * rowSize;
                if (!names.TryGetValue(Marshal.ReadInt32(row + pidOff), out var name)) continue;
                m[(tcp, Port(row + portOff))] = name;
                onRow?.Invoke(name, row);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
