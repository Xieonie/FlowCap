using System.Text.Json;
using SpeedLimit;

public class SettingsTests
{
    [Fact]
    public void OldFormatWithoutBlocked_KeepsLimits()
    {
        const string json = """{"Limits":{"brave":{"DownKBs":5120,"UpKBs":1024}}}""";
        var s = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(new Limit(5120, 1024), s.Limits["brave"]);
        Assert.Equal(60, s.GraphSeconds); // fehlende neue Felder bekommen ihre Standardwerte
    }

    [Fact]
    public void RoundTrip_KeepsBlockedAndTotalLimit()
    {
        var s = new Settings { TotalLimit = new Limit(10240, 0), UseBits = true };
        s.Limits["curl"] = new Limit(0, 0, Blocked: true);
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.True(back.Limits["curl"].Blocked);
        Assert.Equal(new Limit(10240, 0), back.TotalLimit);
        Assert.True(back.UseBits);
    }
}
