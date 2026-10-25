using SpeedLimit;

public class ThrottlerTests
{
    [Fact]
    public void TokenBucket_AllowsBurstThenRefillsAtRate()
    {
        var b = new TokenBucket(1024 * 1024, 0); // 1 MB/s, Burst 0,2 s = 209715 Bytes
        Assert.True(b.TryTake(209715, 0));
        Assert.False(b.TryTake(1500, 0));
        Assert.True(b.TryTake(100 * 1024, 0.11));   // ~110 KB nachgefüllt
        Assert.False(b.TryTake(300 * 1024, 100));   // nie mehr als die Burst-Kapazität
    }

    [Fact]
    public void TokenBucket_LowRateStillFitsLargePackets()
    {
        var b = new TokenBucket(1024, 0); // 1 KB/s
        Assert.True(b.TryTake(64 * 1024, 0));
    }

    static readonly byte[] Ipv4Tcp =
    [
        0x45, 0, 0, 40, 0, 0, 0, 0, 64, 6, 0, 0, 192, 168, 0, 2, 1, 1, 1, 1, // IPv4, IHL 5, TCP
        0x1F, 0x90, 0x01, 0xBB, // Quellport 8080, Zielport 443
        0, 0, 0, 0, 0, 0, 0, 0, 0x50, 0, 0, 0, 0, 0, 0, 0,
    ];

    [Fact]
    public void LocalPort_Ipv4Tcp_UsesSourceForOutboundAndDestForInbound()
    {
        Assert.True(Throttler.TryGetLocalPort(Ipv4Tcp, outbound: true, out var tcp, out var port));
        Assert.True(tcp);
        Assert.Equal(8080, port);
        Assert.True(Throttler.TryGetLocalPort(Ipv4Tcp, outbound: false, out _, out port));
        Assert.Equal(443, port);
    }

    [Fact]
    public void LocalPort_Ipv6Udp()
    {
        var p = new byte[48];
        p[0] = 0x60;
        p[6] = 17; // UDP
        p[40] = 0x00; p[41] = 0x35; // Quellport 53
        p[42] = 0xC3; p[43] = 0x50; // Zielport 50000
        Assert.True(Throttler.TryGetLocalPort(p, outbound: false, out var tcp, out var port));
        Assert.False(tcp);
        Assert.Equal(50000, port);
    }

    [Fact]
    public void LocalPort_RejectsTruncatedAndNonTcpUdp()
    {
        Assert.False(Throttler.TryGetLocalPort(Ipv4Tcp.AsSpan(0, 22), true, out _, out _));
        var icmp = (byte[])Ipv4Tcp.Clone();
        icmp[9] = 1;
        Assert.False(Throttler.TryGetLocalPort(icmp, true, out _, out _));
    }
}
