using System.Text.Json;
using SpeedLimit;

public class DashboardLayoutTests
{
    static DashboardLayout Fresh() => DashboardLayout.Default();

    static void AssertNoOverlaps(DashboardLayout l)
    {
        var visible = l.Tiles.Where(t => !t.Hidden).ToList();
        foreach (var a in visible)
        {
            Assert.InRange(a.X + a.W, 1, DashboardLayout.Columns);
            Assert.InRange(a.Y + a.H, 1, DashboardLayout.MaxRows);
            foreach (var b in visible.Where(b => b != a)) Assert.False(a.Overlaps(b), $"{a.Id} überlappt {b.Id}");
        }
    }

    [Fact]
    public void Move_DisplacedModuleSwitchesSidesInsteadOfLeavingTheScreen()
    {
        var l = Fresh();
        Assert.True(l.Move("details", 0, 6));            // Details nach links ziehen
        Assert.Equal((0, 6), (l.Get("details").X, l.Get("details").Y));
        Assert.Equal((4, 6), (l.Get("apps").X, l.Get("apps").Y)); // Liste weicht nach rechts aus, nicht nach unten
        Assert.True(l.Tiles.Max(t => t.Y + t.H) <= DashboardLayout.VisibleRows); // alles bleibt im Fenster
        AssertNoOverlaps(l);
    }

    [Fact]
    public void Move_GrowsDownOnlyWhenNothingElseFits()
    {
        var l = Fresh();
        Assert.True(l.Move("details", 0, 0));            // oben ist kein Platz für den Graphen daneben
        Assert.Equal((0, 0), (l.Get("details").X, l.Get("details").Y));
        AssertNoOverlaps(l);
    }

    [Fact]
    public void Resize_RespectsMinimumAndGrid()
    {
        var l = Fresh();
        Assert.True(l.Resize("details", 1, 1));          // wird auf Mindestgröße angehoben
        Assert.Equal((3, 3), (l.Get("details").W, l.Get("details").H));
        AssertNoOverlaps(l);
    }

    [Fact]
    public void ChangeThatDoesNotFit_IsRejectedAndLayoutUnchanged()
    {
        var l = Fresh();
        var before = l.Tiles.ToList();
        Assert.False(l.Resize("graph", 12, 44));         // schöbe Liste/Details über die Höchstgrenze
        Assert.Equal(before, l.Tiles);
    }

    [Fact]
    public void HideAndShow_FindsFreeSpot()
    {
        var l = Fresh();
        l.Hide("stats");
        Assert.True(l.Get("stats").Hidden);
        Assert.True(l.Show("stats"));
        Assert.False(l.Get("stats").Hidden);
        AssertNoOverlaps(l);
    }

    [Fact]
    public void Normalize_OldOrBrokenSettingsFallBackSafely()
    {
        // Format der vorigen Version (Bereiche statt Raster) → Standard
        var old = JsonSerializer.Deserialize<DashboardLayout>("""{"Top":["graph"],"Left":["apps"]}""")!;
        old.Normalize();
        Assert.Equal(DashboardLayout.Default().Tiles, old.Tiles);

        // Unbekanntes Modul raus, fehlende ausgeblendet ergänzt, zu große Kacheln begrenzt
        var broken = new DashboardLayout { Tiles = [new("graph", 5, 0, 30, 2), new("alt", 0, 0, 2, 2)] };
        broken.Normalize();
        Assert.Equal(4, broken.Tiles.Count);
        Assert.Equal(12, broken.Get("graph").W);
        Assert.True(broken.Get("apps").Hidden);
        AssertNoOverlaps(broken);
    }
}
