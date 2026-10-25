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
    public void Mbps_FromBytesAndTime()
    {
        Assert.Equal(80, SpeedTest.Mbps(10_000_000, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, SpeedTest.Mbps(1, TimeSpan.Zero));
    }
}
