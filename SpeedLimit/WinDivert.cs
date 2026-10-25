using System.Runtime.InteropServices;

namespace SpeedLimit;

/// <summary>WINDIVERT_ADDRESS (80 Bytes). Felder ab Offset 16 gehören zur Union; hier nur, was FlowCap braucht.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Flags; // Layer:8 Event:8 Sniffed:1 Outbound:1 Loopback:1 Impostor:1 IPv6:1 ...
    // Socket-Layer (WINDIVERT_DATA_SOCKET): ProcessId, LocalPort, Protocol – Ports in Host-Byte-Order
    [FieldOffset(32)] public uint ProcessId;
    [FieldOffset(68)] public ushort LocalPort;
    [FieldOffset(72)] public byte Protocol;

    public bool Outbound
    {
        readonly get => (Flags & (1u << 17)) != 0;
        set => Flags = value ? Flags | (1u << 17) : Flags & ~(1u << 17);
    }

    public readonly bool IPv6 => (Flags & (1u << 20)) != 0;
}

static class WinDivert
{
    const string Dll = "WinDivert.dll";
    public const int LayerNetwork = 0, LayerSocket = 3;
    public const ulong FlagSniff = 1, FlagRecvOnly = 4;

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertRecv(IntPtr handle, byte[]? packet, uint packetLen, out uint recvLen, out WinDivertAddress addr);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, in WinDivertAddress addr);

    [DllImport(Dll)]
    public static extern bool WinDivertHelperCalcChecksums(byte[] packet, uint packetLen, ref WinDivertAddress addr, ulong flags);

    [DllImport(Dll)]
    public static extern bool WinDivertShutdown(IntPtr handle, int how);

    [DllImport(Dll)]
    public static extern bool WinDivertClose(IntPtr handle);
}
