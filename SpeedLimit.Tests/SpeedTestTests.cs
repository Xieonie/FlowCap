using SpeedLimit;

public class SpeedTestTests
{
    [Fact]
    public void Jitter_IsMeanDifferenceOfNeighbours()
    {
        Assert.Equal(0, SpeedTest.Jitter([20]));
        Assert.Equal(10, SpeedTest.Jitter([20, 30, 20]));
    }

    [Fact]
    public void PingStats_CountLossAndIgnoreLostForTimes()
    {
        var s = SpeedTest.PingStats.Of([20, null, 30, 10]);
        Assert.Equal((4, 25.0, 10.0, 20.0, 30.0, 15.0), (s.Sent, s.LossPercent, s.Min!.Value, s.Avg!.Value, s.Max!.Value, s.Jitter));

        var dead = SpeedTest.PingStats.Of([null, null]);
        Assert.Equal((100.0, (double?)null), (dead.LossPercent, dead.Avg));
    }

    [Theory]
    [InlineData("Cloudflare (1.1.1.1)", "1.1.1.1")]
    [InlineData("  example.com ", "example.com")]
    public void HostOf_TakesAddressFromPreset(string target, string host) => Assert.Equal(host, SpeedTest.HostOf(target));

    [Fact]
    public void Mbps_FromBytesAndTime()
    {
        Assert.Equal(80, SpeedTest.Mbps(10_000_000, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, SpeedTest.Mbps(1, TimeSpan.Zero));
    }
}
