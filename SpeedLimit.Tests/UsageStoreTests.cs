using SpeedLimit;

public class UsageStoreTests
{
    static readonly DateOnly Day = new(2026, 10, 24);

    [Fact]
    public void Totals_SumsAcrossDaysAndCalls()
    {
        var s = new UsageStore();
        s.Add(Day, "brave", 100, 10);
        s.Add(Day, "brave", 50, 5);
        s.Add(Day.AddDays(-1), "brave", 1000, 0);
        s.Add(Day.AddDays(-1), "steam", 7, 3);

        Assert.Equal((150L, 15L), s.Totals(Day, Day)["brave"]);
        var week = s.Totals(Day.AddDays(-6), Day);
        Assert.Equal((1150L, 15L), week["brave"]);
        Assert.Equal((7L, 3L), week["steam"]);
    }

    [Fact]
    public void Prune_RemovesOldDaysOnly()
    {
        var s = new UsageStore();
        s.Add(Day.AddDays(-100), "a", 1, 1);
        s.Add(Day, "a", 2, 2);
        s.Prune(Day.AddDays(-90));
        Assert.Single(s.Days);
        Assert.Equal((2L, 2L), s.Totals(Day.AddDays(-200), Day)["a"]);
    }

    [Fact]
    public void Add_IgnoresZeroTraffic()
    {
        var s = new UsageStore();
        s.Add(Day, "idle", 0, 0);
        Assert.Empty(s.Days);
    }
}
