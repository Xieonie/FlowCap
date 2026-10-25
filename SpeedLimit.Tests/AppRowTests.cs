using SpeedLimit;

public class AppRowTests
{
    [Fact]
    public void Slider_EndpointsAndMonotonic()
    {
        Assert.Equal(10, AppRow.SliderToKBs(0));
        Assert.Equal(100 * 1024 - 2 * 1024, AppRow.SliderToKBs(1000)); // 97,66 MB/s → 98 MB/s
        var prev = 0;
        for (var s = 0; s <= 1000; s += 5)
        {
            var kbs = AppRow.SliderToKBs(s);
            Assert.True(kbs >= prev, $"nicht monoton bei {s}");
            prev = kbs;
        }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1024)]
    [InlineData(5120)]
    [InlineData(10240)]
    public void Slider_PresetsRoundTrip(int kbs) => Assert.Equal(kbs, AppRow.SliderToKBs(AppRow.KBsToSlider(kbs)));

    [Fact]
    public void Limited_ToggleKeepsLastValue()
    {
        var row = new AppRow("x", null, default, _ => { });
        row.LimitDown = 5120;
        row.DownLimited = false;
        Assert.Null(row.LimitDown);
        row.DownLimited = true;
        Assert.Equal(5120, row.LimitDown);
    }

    [Fact]
    public void Blocked_IsPersistedInLimitAndClearedByClearLimits()
    {
        var row = new AppRow("x", null, default, _ => { });
        row.IsBlocked = true;
        Assert.True(row.IsLimited);
        Assert.Equal(new Limit(0, 0, true), row.Limit);
        Assert.Equal("Gesperrt", row.Badge);
        row.ClearLimits();
        Assert.Equal(default, row.Limit);
    }

    [Fact]
    public void Cap_ToggleKeepsChosenValuesAndReportsChange()
    {
        var row = new AppRow("x", null, default, _ => { });
        var changes = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppRow.Cap)) changes++; };
        row.CapPresetIndex = 2;   // 1 GB
        row.CapPeriodIndex = 0;   // pro Tag
        row.CapBlock = true;
        Assert.Null(row.Cap);     // noch nicht aktiv
        row.CapEnabled = true;
        Assert.Equal(new DataCap(1L << 30, Monthly: false, Block: true), row.Cap);
        row.CapEnabled = false;
        row.CapEnabled = true;
        Assert.Equal(1L << 30, row.Cap!.Value.Bytes);
        Assert.Equal(6, changes);
    }

    [Fact]
    public void DataCap_PeriodStartAndKey()
    {
        var today = new DateOnly(2026, 10, 24);
        Assert.Equal(new DateOnly(2026, 10, 1), new DataCap(1, Monthly: true, false).PeriodStart(today));
        Assert.Equal(today, new DataCap(1, Monthly: false, false).PeriodStart(today));
        Assert.Equal("2026-10", new DataCap(1, true, false).PeriodKey(today));
    }

    [Theory]
    [InlineData(1024.0, false, "1,0 MB/s")]
    [InlineData(1024.0, true, "8,39 Mbit/s")]
    [InlineData(12.5, true, "102,4 kbit/s")]
    public void FormatRate_Units(double kbs, bool bits, string expected)
    {
        var old = AppRow.UseBits;
        try
        {
            AppRow.UseBits = bits;
            Assert.Equal(expected, AppRow.FormatRate(kbs).Replace('.', ','));
        }
        finally
        {
            AppRow.UseBits = old;
        }
    }

    [Fact]
    public void Update_KeepsFixedHistoryLength()
    {
        var row = new AppRow("x", null, default, _ => { });
        for (var i = 0; i < 100; i++) row.Update(i, 0);
        Assert.Equal(AppRow.HistoryLength, row.DownHistory.Length);
        Assert.Equal(99, row.DownHistory[^1]);
        Assert.Equal(60, row.DownRecent.Length);
        Assert.Equal(99, row.DownRecent[^1]);
    }
}
