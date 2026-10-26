using SpeedLimit;

public class PacketInfoTests
{
    // IPv4/TCP SYN 192.168.0.2:50000 → 1.1.1.1:443
    static byte[] Syn() =>
    [
        0x45, 0, 0, 40, 0, 0, 0, 0, 64, 6, 0, 0, 192, 168, 0, 2, 1, 1, 1, 1,
        0xC3, 0x50, 0x01, 0xBB, 0, 0, 0, 0, 0, 0, 0, 0, 0x50, 0x02, 0, 0, 0, 0, 0, 0,
    ];

    [Fact]
    public void Parse_TcpSyn()
    {
        Assert.True(PacketInfo.TryParse(Syn(), out var p));
        Assert.True(p.V4 && p.Tcp && p.Syn);
        Assert.Equal(50000, p.SrcPort);
        Assert.Equal(443, p.DstPort);
    }

    [Fact]
    public void Reflect_ToRelayAndBack_RestoresOriginalEndpoints()
    {
        var pkt = Syn();
        PacketInfo.TryParse(pkt, out var p);
        p.ReflectToRelay(pkt, 40000);
        PacketInfo.TryParse(pkt, out var toRelay);
        // Relay sieht: Absender 1.1.1.1:50000, Ziel 192.168.0.2:40000
        Assert.Equal(new byte[] { 1, 1, 1, 1, 192, 168, 0, 2 }, pkt[12..20]);
        Assert.Equal((50000, 40000), ((int)toRelay.SrcPort, (int)toRelay.DstPort));

        // Antwort des Relays: 192.168.0.2:40000 → 1.1.1.1:50000
        var reply = Syn();
        reply[20] = 0x9C; reply[21] = 0x40; // 40000
        reply[22] = 0xC3; reply[23] = 0x50; // 50000
        PacketInfo.TryParse(reply, out var r);
        r.ReflectFromRelay(reply, 443);
        PacketInfo.TryParse(reply, out var back);
        // Programm sieht: Absender 1.1.1.1:443, Ziel 192.168.0.2:50000
        Assert.Equal(new byte[] { 1, 1, 1, 1, 192, 168, 0, 2 }, reply[12..20]);
        Assert.Equal((443, 50000), ((int)back.SrcPort, (int)back.DstPort));
    }

    // TCP-SYN mit MSS-Option 1460 (Header 24 Byte)
    static byte[] SynWithMss()
    {
        var p = new byte[44];
        Syn().AsSpan(0, 20).CopyTo(p);
        p[3] = 44;
        Syn().AsSpan(20, 20).CopyTo(p.AsSpan(20));
        p[32] = 0x60;                         // Header 24 Byte
        p[40] = 2; p[41] = 4; p[42] = 0x05; p[43] = 0xB4; // MSS 1460
        PacketInfo.FixChecksums(p);
        return p;
    }

    [Fact]
    public void ClampMss_LowersOnlyLargerValues()
    {
        var p = SynWithMss();
        PacketInfo.ClampMss(p, 1380);
        Assert.Equal(1380, (p[42] << 8) | p[43]);
        PacketInfo.ClampMss(p, 1400);
        Assert.Equal(1380, (p[42] << 8) | p[43]);
    }

    [Fact]
    public void FixChecksums_ProducesVerifiableChecksums()
    {
        var p = SynWithMss();
        Assert.True(ChecksumsValid(p));
        PacketInfo.SetSource(p, [10, 2, 0, 2]);
        Assert.False(ChecksumsValid(p));
        PacketInfo.FixChecksums(p);
        Assert.True(ChecksumsValid(p));
    }

    [Fact]
    public void Segment_SplitsPayloadWithCorrectSequenceAndFlags()
    {
        var big = new byte[40 + 3000];
        Syn().AsSpan(0, 40).CopyTo(big);
        big[2] = (byte)(big.Length >> 8); big[3] = (byte)big.Length;
        big[33] = 0x19; // ACK + PSH + FIN
        for (var i = 40; i < big.Length; i++) big[i] = (byte)i;
        PacketInfo.FixChecksums(big);

        var parts = PacketInfo.Segment(big, 1420);
        Assert.Equal(3, parts.Count);                         // 1380 + 1380 + 240 Nutzdaten
        Assert.All(parts, s => Assert.True(s.Length <= 1420 && ChecksumsValid(s)));
        // Testpaket startet bei Sequenznummer 0 → zweites Stück bei 1380
        Assert.Equal(1380u, (uint)((parts[1][24] << 24) | (parts[1][25] << 16) | (parts[1][26] << 8) | parts[1][27]));
        Assert.Equal(0x10, parts[0][33]);                     // nur ACK
        Assert.Equal(0x19, parts[2][33]);                     // letztes Stück behält PSH/FIN
        Assert.Equal(big[40..], parts.SelectMany(s => s[40..]).ToArray());
    }

    // Summe über Kopf bzw. Pseudo-Header + Segment muss 0xFFFF ergeben
    static bool ChecksumsValid(byte[] p)
    {
        static uint Sum(ReadOnlySpan<byte> d, uint s = 0)
        {
            for (var i = 0; i + 1 < d.Length; i += 2) s += (uint)((d[i] << 8) | d[i + 1]);
            if (d.Length % 2 == 1) s += (uint)d[^1] << 8;
            while (s >> 16 != 0) s = (s & 0xFFFF) + (s >> 16);
            return s;
        }
        var ihl = (p[0] & 0xF) * 4;
        if (Sum(p.AsSpan(0, ihl)) != 0xFFFF) return false;
        var l4 = p.AsSpan(ihl);
        uint pseudo = 0;
        for (var i = 12; i < 20; i += 2) pseudo += (uint)((p[i] << 8) | p[i + 1]);
        pseudo += p[9];
        pseudo += (uint)l4.Length;
        return Sum(l4, pseudo) == 0xFFFF;
    }

    // IPv6-TCP: fd00::2 → 2001:4860::8888, 20 Byte TCP-Kopf + Nutzdaten
    static byte[] V6Tcp(int payload)
    {
        var p = new byte[60 + payload];
        p[0] = 0x60;
        p[4] = (byte)((20 + payload) >> 8); p[5] = (byte)(20 + payload);
        p[6] = 6; p[7] = 64;
        p[8] = 0xfd; p[23] = 2;
        p[24] = 0x20; p[25] = 0x01; p[26] = 0x48; p[27] = 0x60; p[38] = 0x88; p[39] = 0x88;
        p[40] = 0xC0; p[41] = 0x01; p[43] = 80;   // Ports 49153 → 80
        p[52] = 0x50; p[53] = 0x18;               // Kopf 20 Byte, ACK + PSH
        for (var i = 60; i < p.Length; i++) p[i] = (byte)i;
        return p;
    }

    static bool V6ChecksumValid(byte[] p)
    {
        uint s = 0;
        for (var i = 8; i < 40; i += 2) s += (uint)((p[i] << 8) | p[i + 1]);
        s += 6;
        var l4 = p.AsSpan(40);
        s += (uint)l4.Length;
        for (var i = 0; i + 1 < l4.Length; i += 2) s += (uint)((l4[i] << 8) | l4[i + 1]);
        if (l4.Length % 2 == 1) s += (uint)l4[^1] << 8;
        while (s >> 16 != 0) s = (s & 0xFFFF) + (s >> 16);
        return s == 0xFFFF;
    }

    [Fact]
    public void V6_RewriteFixAndSegment()
    {
        var p = V6Tcp(3000);
        Assert.True(PacketInfo.TryParse(p, out var info));
        Assert.Equal("2001:4860::8888", info.RemoteAddress(p, outbound: true).ToString());

        PacketInfo.SetSource(p, System.Net.IPAddress.Parse("fd00::99").GetAddressBytes());
        PacketInfo.FixChecksums(p);
        Assert.True(V6ChecksumValid(p));
        Assert.Equal("fd00::99", new System.Net.IPAddress(info.Source(p)).ToString());

        var parts = PacketInfo.Segment(p, 1420);
        Assert.Equal(3, parts.Count);                                  // 1360 + 1360 + 280 Nutzdaten
        Assert.All(parts, s => Assert.True(s.Length <= 1420 && V6ChecksumValid(s) && ((s[4] << 8) | s[5]) == s.Length - 40));
        Assert.Equal(p[60..], parts.SelectMany(s => s[60..]).ToArray());
    }

    [Fact]
    public void RemoteAddress_IsDestinationWhenSendingAndSourceWhenReceiving()
    {
        var p = Syn(); // 192.168.0.2 → 1.1.1.1
        Assert.True(PacketInfo.TryParse(p, out var info));
        Assert.Equal("1.1.1.1", info.RemoteAddress(p, outbound: true).ToString());
        Assert.Equal("192.168.0.2", info.RemoteAddress(p, outbound: false).ToString());
    }

    [Fact]
    public void AckIsNotSyn()
    {
        var pkt = Syn();
        pkt[33] = 0x12; // SYN+ACK
        PacketInfo.TryParse(pkt, out var p);
        Assert.False(p.Syn);
    }
}
