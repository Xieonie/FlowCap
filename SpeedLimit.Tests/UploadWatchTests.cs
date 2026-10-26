using SpeedLimit;

public class UploadWatchTests
{
    const long MB = 1_000_000;
    static readonly DateTime T0 = new(2026, 10, 26, 12, 0, 0);

    [Fact]
    public void WarnsOnceWhenWindowExceedsThreshold()
    {
        var w = new UploadWatch();
        Assert.Null(w.Add("app", 60 * MB, T0, 100 * MB));
        Assert.Equal(120 * MB, w.Add("app", 60 * MB, T0.AddMinutes(5), 100 * MB));
        Assert.Null(w.Add("app", 200 * MB, T0.AddMinutes(6), 100 * MB)); // heute schon gemeldet
        Assert.NotNull(w.Add("app", 200 * MB, T0.AddDays(1), 100 * MB)); // neuer Tag
    }

    [Fact]
    public void OldUploadsLeaveTheWindow()
    {
        var w = new UploadWatch();
        Assert.Null(w.Add("app", 60 * MB, T0, 100 * MB));
        Assert.Null(w.Add("app", 60 * MB, T0.AddMinutes(11), 100 * MB)); // die ersten 60 MB sind älter als 10 Minuten
        Assert.Null(w.Add("other", 99 * MB, T0, 100 * MB));              // Programme werden getrennt gezählt
    }
}
