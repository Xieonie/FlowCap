using System.Buffers.Binary;

namespace SpeedLimit;

/// <summary>Die Kopfdaten eines IPv4/IPv6-TCP/UDP-Pakets, die für Drosselung und Proxy-Umleitung gebraucht werden.</summary>
public readonly record struct PacketInfo(bool V4, byte Proto, int L4Offset, ushort SrcPort, ushort DstPort, byte TcpFlags)
{
    public bool Tcp => Proto == 6;
    public bool Udp => Proto == 17;
    /// <summary>Verbindungsaufbau (SYN ohne ACK).</summary>
    public bool Syn => Tcp && (TcpFlags & 0x12) == 0x02;

    public static bool TryParse(ReadOnlySpan<byte> p, out PacketInfo info)
    {
        info = default;
        if (p.Length < 20) return false;
        int off;
        byte proto;
        var v4 = p[0] >> 4 == 4;
        if (v4) { off = (p[0] & 0xF) * 4; proto = p[9]; }
        else if (p[0] >> 4 == 6 && p.Length >= 40) { off = 40; proto = p[6]; } // shortcut: IPv6-Extension-Header werden nicht ausgewertet, selten bei TCP/UDP
        else return false;
        if ((proto != 6 && proto != 17) || p.Length < off + (proto == 6 ? 14 : 4)) return false;
        info = new(v4, proto, off,
            BinaryPrimitives.ReadUInt16BigEndian(p[off..]),
            BinaryPrimitives.ReadUInt16BigEndian(p[(off + 2)..]),
            proto == 6 ? p[off + 13] : (byte)0);
        return true;
    }

    /// <summary>
    /// Ausgehendes IPv4-Paket des Programms (L:lp → R:rp) als eingehendes Paket an das Relay spiegeln: R:lp → L:relay.
    /// Prüfsummen und Richtung muss der Aufrufer anpassen.
    /// </summary>
    public void ReflectToRelay(Span<byte> p, ushort relayPort)
    {
        SwapAddresses(p);
        BinaryPrimitives.WriteUInt16BigEndian(p[(L4Offset + 2)..], relayPort);
    }

    /// <summary>Antwort des Relays (L:relay → R:lp) zurück zum Programm spiegeln: R:rp → L:lp.</summary>
    public void ReflectFromRelay(Span<byte> p, ushort originalPort)
    {
        SwapAddresses(p);
        BinaryPrimitives.WriteUInt16BigEndian(p[L4Offset..], originalPort);
    }

    public static void SetSource(Span<byte> p, ReadOnlySpan<byte> ip4) => ip4.CopyTo(p[12..16]);

    public static void SetDestination(Span<byte> p, ReadOnlySpan<byte> ip4) => ip4.CopyTo(p[16..20]);

    /// <summary>MSS-Option eines TCP-SYN (bzw. SYN+ACK) auf höchstens <paramref name="maxMss"/> begrenzen, damit Segmente samt Tunnel-Hülle durchpassen.</summary>
    public static void ClampMss(Span<byte> p, int maxMss)
    {
        if (!TryParse(p, out var i) || !i.Tcp || (i.TcpFlags & 0x02) == 0) return;
        var tcp = p[i.L4Offset..];
        var headerLen = (tcp[12] >> 4) * 4;
        for (var o = 20; o + 1 < headerLen && o < tcp.Length;)
        {
            var kind = tcp[o];
            if (kind == 0) break;           // Ende der Optionen
            if (kind == 1) { o++; continue; } // NOP
            var len = tcp[o + 1];
            if (len < 2) break;
            if (kind == 2 && len == 4 && BinaryPrimitives.ReadUInt16BigEndian(tcp[(o + 2)..]) > maxMss)
                BinaryPrimitives.WriteUInt16BigEndian(tcp[(o + 2)..], (ushort)maxMss);
            o += len;
        }
    }

    /// <summary>
    /// Zu großes IPv4-TCP-Paket (z. B. von der Segmentierungs-Auslagerung der Netzwerkkarte) in Pakete bis <paramref name="mtu"/> Byte zerlegen.
    /// Prüfsummen sind danach korrekt. Andere Pakete kommen unverändert zurück.
    /// </summary>
    public static List<byte[]> Segment(byte[] p, int mtu)
    {
        if (p.Length <= mtu || !TryParse(p, out var i) || !i.V4 || !i.Tcp) return [p];
        var headers = i.L4Offset + (p[i.L4Offset + 12] >> 4) * 4;
        var chunk = mtu - headers;
        if (chunk <= 0) return [p];
        var payload = p.AsSpan(headers);
        var seq = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i.L4Offset + 4));
        var ipId = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
        var result = new List<byte[]>();
        for (var off = 0; off < payload.Length; off += chunk)
        {
            var n = Math.Min(chunk, payload.Length - off);
            var seg = new byte[headers + n];
            p.AsSpan(0, headers).CopyTo(seg);
            payload.Slice(off, n).CopyTo(seg.AsSpan(headers));
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)seg.Length);
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(4), (ushort)(ipId + result.Count));
            BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(i.L4Offset + 4), seq + (uint)off);
            if (off + n < payload.Length) seg[i.L4Offset + 13] &= unchecked((byte)~0x09); // FIN/PSH nur am letzten Stück
            FixChecksums(seg);
            result.Add(seg);
        }
        return result;
    }

    /// <summary>IPv4-Kopf- und TCP/UDP-Prüfsumme neu berechnen (nach dem Umschreiben von Adressen/Ports).</summary>
    public static void FixChecksums(Span<byte> p)
    {
        var ihl = (p[0] & 0xF) * 4;
        var total = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        p[10] = p[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(p[10..], Checksum(0, p[..ihl]));

        var proto = p[9];
        var l4 = p[ihl..total];
        var csumOffset = proto switch { 6 => 16, 17 => 6, _ => -1 };
        if (csumOffset < 0 || l4.Length < csumOffset + 2) return;
        l4[csumOffset] = l4[csumOffset + 1] = 0;
        // Pseudo-Header: Quell-/Zieladresse, Protokoll, Länge
        uint sum = 0;
        for (var i = 12; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(p[i..]);
        sum += proto;
        sum += (uint)l4.Length;
        var c = Checksum(sum, l4);
        if (proto == 17 && c == 0) c = 0xFFFF; // UDP: 0 hieße "keine Prüfsumme"
        BinaryPrimitives.WriteUInt16BigEndian(l4[csumOffset..], c);
    }

    static ushort Checksum(uint sum, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if (data.Length % 2 == 1) sum += (uint)data[^1] << 8;
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    static void SwapAddresses(Span<byte> p)
    {
        Span<byte> tmp = stackalloc byte[4];
        p[12..16].CopyTo(tmp);
        p[16..20].CopyTo(p[12..16]);
        tmp.CopyTo(p[16..20]);
    }
}
