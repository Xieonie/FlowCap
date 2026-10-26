using SpeedLimit;

public class PingLogTests
{
    static readonly DateTime T0 = new(2026, 10, 26, 12, 0, 0);

    [Fact]
    public void Add_GroupsIntoFiveMinuteBuckets()
    {
        var log = new PingLog();
        log.Add(T0, 10);
        log.Add(T0.AddMinutes(1), 30);
        log.Add(T0.AddMinutes(2), null);
        log.Add(T0.AddMinutes(5), 50); // neuer Abschnitt

        Assert.Equal(2, log.Buckets.Count);
        var b = log.Buckets[0];
        Assert.Equal((3, 1, 20.0, 30.0), (b.Sent, b.Lost, b.Avg!.Value, b.Max!.Value));
        Assert.Equal(100 / 3.0, b.LossPercent, 3);
    }

    [Fact]
    public void Range_MergesBucketsAndLeavesGapsEmpty()
    {
        var log = new PingLog();
        log.Add(T0, 10);                 // 12:00
        log.Add(T0.AddMinutes(5), 20);   // 12:05
        log.Add(T0.AddMinutes(5), 20);
        log.Add(T0.AddHours(2), null);   // 14:00, nur Verlust

        var hours = log.Range(T0, TimeSpan.FromHours(1), 3);
        Assert.Equal((3, 0, 50 / 3.0, 20.0), (hours[0]!.Sent, hours[0]!.Lost, hours[0]!.Avg!.Value, hours[0]!.Max!.Value));
        Assert.Null(hours[1]);           // keine Messung, nicht 0 ms
        Assert.Equal((1, 1, (double?)null), (hours[2]!.Sent, hours[2]!.Lost, hours[2]!.Avg));
    }
}
