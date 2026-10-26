namespace SpeedLimit;

/// <summary>Position und Größe eines Live-Moduls im Raster (in Feldern).</summary>
public sealed record Tile(string Id, int X, int Y, int W, int H, bool Hidden = false)
{
    public bool Overlaps(Tile o) => X < o.X + o.W && o.X < X + W && Y < o.Y + o.H && o.Y < Y + H;
}

/// <summary>
/// Anordnung der Live-Module in einem Raster mit <see cref="Columns"/> Spalten; <see cref="VisibleRows"/> Zeilen füllen das Fenster,
/// darunter darf das Raster bis <see cref="MaxRows"/> wachsen (dann wird gescrollt). Verschieben/Größe ändern schiebt überlappende Module nach unten.
/// </summary>
public sealed class DashboardLayout
{
    public const int Columns = 12, VisibleRows = 12, MaxRows = 48;
    public static readonly string[] Modules = ["graph", "stats", "apps", "details"];

    public List<Tile> Tiles { get; set; } = [];

    public static DashboardLayout Default() => new()
    {
        Tiles =
        [
            new("graph", 0, 0, 12, 4),
            new("stats", 0, 4, 12, 2),
            new("apps", 0, 6, 8, 6),
            new("details", 8, 6, 4, 6),
        ],
    };

    public static (int W, int H) MinSize(string module) => module switch
    {
        "apps" => (4, 3),
        "details" => (3, 3),
        _ => (3, 2),
    };

    public static string Title(string module) => module switch
    {
        "graph" => "Live-Graph",
        "stats" => "Tacho",
        "apps" => "Programmliste",
        _ => "Details",
    };

    public Tile Get(string id) => Tiles.First(t => t.Id == id);

    public DashboardLayout Clone() => new() { Tiles = [.. Tiles] };

    /// <summary>Gespeicherte (auch alte/kaputte) Layouts reparieren; ist nichts Brauchbares da, gilt der Standard.</summary>
    public void Normalize()
    {
        var known = Tiles.Where(t => Modules.Contains(t.Id)).DistinctBy(t => t.Id).Select(Clamp).ToList();
        if (known.Count == 0) known = Default().Tiles;
        Tiles = known;
        foreach (var m in Modules.Where(m => Tiles.All(t => t.Id != m))) Tiles.Add(Default().Get(m) with { Hidden = true });
        if (!Resolve(Tiles.FirstOrDefault(t => !t.Hidden)?.Id ?? "")) Tiles = Default().Tiles;
    }

    /// <summary>Modul an Feld (x, y) verschieben. false = passt nicht, Layout unverändert.</summary>
    public bool Move(string id, int x, int y) => Apply(Get(id) with { X = x, Y = y });

    /// <summary>Größe ändern (in Feldern, mindestens <see cref="MinSize"/>). false = passt nicht, Layout unverändert.</summary>
    public bool Resize(string id, int w, int h) => Apply(Get(id) with { W = w, H = h });

    public void Hide(string id) => Replace(Get(id) with { Hidden = true });

    /// <summary>Ausgeblendetes Modul an einer freien Stelle wieder einblenden (notfalls in Mindestgröße).</summary>
    public bool Show(string id)
    {
        var t = Get(id);
        var (minW, minH) = MinSize(id);
        foreach (var (w, h) in new[] { (t.W, t.H), (minW, minH) })
            for (var y = 0; y + h <= MaxRows; y++)
                for (var x = 0; x + w <= Columns; x++)
                {
                    var spot = t with { X = x, Y = y, W = w, H = h, Hidden = false };
                    if (Tiles.Where(o => !o.Hidden && o.Id != id).All(o => !o.Overlaps(spot)))
                    {
                        Replace(spot);
                        return true;
                    }
                }
        return false;
    }

    bool Apply(Tile changed)
    {
        var backup = Tiles;
        Tiles = [.. Tiles];
        Replace(Clamp(changed));
        if (Resolve(changed.Id)) return true;
        Tiles = backup;
        return false;
    }

    /// <summary>
    /// Überlappungen auflösen: das zuletzt bewegte Modul bleibt, verdrängte weichen auf den nächsten freien Platz aus –
    /// bevorzugt in derselben Höhe und innerhalb des sichtbaren Bereichs, erst zuletzt nach unten. false, wenn nichts mehr passt.
    /// </summary>
    bool Resolve(string fixedId)
    {
        var placed = Tiles.Where(t => t.Id == fixedId && !t.Hidden).ToList();
        var bottom = Math.Max(VisibleRows, Tiles.Where(t => !t.Hidden).Select(t => t.Y + t.H).DefaultIfEmpty(0).Max());
        foreach (var t in Tiles.Where(t => t.Id != fixedId && !t.Hidden).OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            var spot = placed.Any(p => p.Overlaps(t)) ? NearestFree(t, placed, bottom) : t;
            if (spot is null) return false;
            placed.Add(spot);
            Replace(spot);
        }
        return true;
    }

    // Freier Platz mit geringstem Abstand; senkrechtes Ausweichen kostet mehr als waagerechtes, Wachsen über den sichtbaren Bereich am meisten
    static Tile? NearestFree(Tile t, List<Tile> placed, int bottom)
    {
        Tile? best = null;
        var bestCost = double.MaxValue;
        for (var y = 0; y + t.H <= MaxRows; y++)
            for (var x = 0; x + t.W <= Columns; x++)
            {
                var c = t with { X = x, Y = y };
                if (placed.Any(p => p.Overlaps(c))) continue;
                var cost = Math.Abs(x - t.X) + 3 * Math.Abs(y - t.Y) + (y + t.H > bottom ? 100 + y : 0);
                if (cost < bestCost) (best, bestCost) = (c, cost);
            }
        return best;
    }

    static Tile Clamp(Tile t)
    {
        var (minW, minH) = MinSize(t.Id);
        var w = Math.Clamp(t.W, minW, Columns);
        var h = Math.Clamp(t.H, minH, MaxRows);
        return t with { W = w, H = h, X = Math.Clamp(t.X, 0, Columns - w), Y = Math.Clamp(t.Y, 0, MaxRows - h) };
    }

    void Replace(Tile t) => Tiles[Tiles.FindIndex(o => o.Id == t.Id)] = t;
}
