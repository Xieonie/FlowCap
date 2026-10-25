using System.Text.Json;
using SpeedLimit;

public class ScheduleTests
{
    // 2026-10-26 ist ein Montag
    static DateTime At(int day, int h, int m) => new(2026, 10, 26 + day, h, m, 0);

    [Fact]
    public void Daytime_WeekdaysOnly()
    {
        var r = new ScheduleRule { From = "09:00", To = "17:00" }; // Standard Mo–Fr
        Assert.True(r.Matches(At(0, 9, 0)));
        Assert.True(r.Matches(At(4, 16, 59)));
        Assert.False(r.Matches(At(0, 17, 0)));  // Ende exklusiv
        Assert.False(r.Matches(At(0, 8, 59)));
        Assert.False(r.Matches(At(5, 12, 0)));  // Samstag
    }

    [Fact]
    public void Overnight_UsesStartDay()
    {
        var r = new ScheduleRule { From = "22:00", To = "6:00", Days = 0 };
        r.Fr = true;
        Assert.True(r.Matches(At(4, 23, 0)));   // Freitagabend
        Assert.True(r.Matches(At(5, 5, 59)));   // Samstagmorgen gehört noch zum Freitag
        Assert.False(r.Matches(At(5, 23, 0)));  // Samstagabend nicht
        Assert.False(r.Matches(At(4, 5, 0)));   // Freitagmorgen gehört zum Donnerstag
    }

    [Fact]
    public void SameFromTo_IsWholeDay_DisabledOrInvalidNeverMatches()
    {
        Assert.True(new ScheduleRule { From = "0:00", To = "00:00" }.Matches(At(2, 13, 37)));
        Assert.False(new ScheduleRule { Enabled = false }.Matches(At(0, 10, 0)));
        var bad = new ScheduleRule { From = "25:00" };
        Assert.False(bad.IsValid);
        Assert.False(bad.Matches(At(0, 10, 0)));
    }

    [Fact]
    public void ActiveRule_FirstMatchWins()
    {
        var a = new ScheduleRule { Profile = "Arbeit", From = "09:00", To = "17:00" };
        var b = new ScheduleRule { Profile = "Ganztag", From = "0:00", To = "0:00", Days = 0b1111111 };
        Assert.Same(a, ScheduleRule.ActiveRule([a, b], At(0, 10, 0)));
        Assert.Same(b, ScheduleRule.ActiveRule([a, b], At(0, 20, 0)));
    }

    [Fact]
    public void DayToggles_RoundTripThroughJson()
    {
        var r = new ScheduleRule { Days = 0 };
        r.Sa = true;
        r.So = true;
        var back = JsonSerializer.Deserialize<ScheduleRule>(JsonSerializer.Serialize(r))!;
        Assert.True(back.Sa && back.So && !back.Mo);
    }

    [Fact]
    public void EnsureProfiles_CreatesStandardFromLiveValues()
    {
        var s = new Settings { TotalLimit = new Limit(100, 0) };
        s.Limits["brave"] = new Limit(5120, 0);
        s.EnsureProfiles();
        Assert.Equal(new Limit(5120, 0), s.Profiles["Standard"].Limits["brave"]);
        Assert.Equal("Standard", s.SelectedProfile);
    }
}
