using SpeedLimit;

public class ThemeTests
{
    public static TheoryData<string> Ids() => [.. Theme.All.Select(t => t.Id)];

    [Theory]
    [MemberData(nameof(Ids))]
    public void Theme_IsReadable(string id)
    {
        var t = Theme.Get(id);
        var card = Theme.Over(t.Panel, t.BgBottom);
        var inner = Theme.Over(t.PanelInner, card);
        // Normaler Text ≥ 4,5:1, Neben-/Großtext ≥ 3:1 (WCAG AA) – auf Karte und innerer Fläche
        Assert.True(Theme.Contrast(t.Ink, inner) >= 4.5, $"{id}: Text auf Fläche");
        Assert.True(Theme.Contrast(Theme.Over(t.Muted, card), card) >= 3, $"{id}: Nebentext auf Karte");
        Assert.True(Theme.Contrast(t.OnAccent, t.Accent) >= 4.5, $"{id}: Text auf Akzent");
        Assert.True(Theme.Contrast(t.Down, card) >= 3 && Theme.Contrast(t.Up, card) >= 3, $"{id}: Graph-Farben auf Karte");
    }

    [Fact]
    public void Ids_AreUnique_AndUnknownFallsBackToDefault()
    {
        Assert.Equal(Theme.All.Count, Theme.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal("graphit", Theme.Get("gibt-es-nicht").Id);
    }
}
