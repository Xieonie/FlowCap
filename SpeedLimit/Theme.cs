using System.Windows.Media;

namespace SpeedLimit;

/// <summary>
/// Farbschema der Oberfläche. Die Paletten sind an Design-Systeme aus github.com/VoltAgent/awesome-design-md angelehnt;
/// die Themes tragen eigene Namen, die Vorlage steht nur als Hinweis in <see cref="Inspiration"/>.
/// </summary>
public sealed record Theme(
    string Id, string Name, string Inspiration, bool IsLight,
    Color BgTop, Color BgBottom, Color Panel, Color PanelInner, Color Hairline,
    Color Ink, Color Muted, Color Accent, Color OnAccent, Color Down, Color Up, Color Danger, Color Dialog, double Radius)
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly IReadOnlyList<Theme> All =
    [
        new("graphit", "Graphit Neon", "FlowCap", false,
            C("#0B0B0F"), C("#16161D"), C("#0FFFFFFF"), C("#0CFFFFFF"), C("#00000000"),
            C("#FFFFFF"), C("#8CFFFFFF"), C("#00E5FF"), C("#0B0B0F"), C("#00E5FF"), C("#A3FF12"), C("#FF4D6D"), C("#1B1B23"), 12),
        new("lavendel", "Lavendel", "inspiriert von Linear", false,
            C("#010102"), C("#08090A"), C("#0F1011"), C("#18191A"), C("#23252A"),
            C("#F7F8F8"), C("#8A8F98"), C("#5E6AD2"), C("#FFFFFF"), C("#828FFF"), C("#D0D6E0"), C("#EB5757"), C("#141516"), 8),
        new("studio", "Studio", "inspiriert von Spotify", false,
            C("#121212"), C("#121212"), C("#181818"), C("#252525"), C("#00000000"),
            C("#FFFFFF"), C("#B3B3B3"), C("#1ED760"), C("#000000"), C("#1ED760"), C("#539DF5"), C("#F3727F"), C("#1F1F1F"), 8),
        new("nachtviolett", "Nachtviolett", "inspiriert von Sentry", false,
            C("#150F23"), C("#1F1633"), C("#10FFFFFF"), C("#0CFFFFFF"), C("#362D59"),
            C("#FFFFFF"), C("#B9B0D6"), C("#C2EF4E"), C("#150F23"), C("#FA7FAA"), C("#C2EF4E"), C("#FF5C7A"), C("#1F1633"), 12),
        new("gold", "Gold", "inspiriert von Lamborghini", false,
            C("#000000"), C("#0C0C0C"), C("#181818"), C("#202020"), C("#2A2A2A"),
            C("#FFFFFF"), C("#9A9A9A"), C("#FFC000"), C("#000000"), C("#FFC000"), C("#29ABE2"), C("#FF4D4D"), C("#181818"), 2),
        new("terrakotta", "Terrakotta (hell)", "inspiriert von Claude", true,
            C("#FAF9F5"), C("#F5F0E8"), C("#EFE9DE"), C("#E8E0D2"), C("#E6DFD8"),
            // Graph-Linien in den dunkleren Tönen der Vorlage, sonst zu blass auf Creme
            C("#141413"), C("#6C6A64"), C("#CC785C"), C("#141413"), C("#A9583E"), C("#4A6D94"), C("#B3261E"), C("#FAF9F5"), 12),
    ];

    public static Theme Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // Farben für die Vorschau-Kacheln in den Einstellungen
    public Brush PreviewBg => Frozen(new LinearGradientBrush(BgTop, BgBottom, 90));
    public Brush PreviewPanel => Frozen(new SolidColorBrush(Over(Panel, BgBottom)));
    public Brush PreviewAccent => Frozen(new SolidColorBrush(Accent));
    public Brush PreviewDown => Frozen(new SolidColorBrush(Down));
    public Brush PreviewUp => Frozen(new SolidColorBrush(Up));
    public Brush PreviewInk => Frozen(new SolidColorBrush(Ink));

    static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Halbtransparente Farbe auf einen Untergrund rechnen.</summary>
    public static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    /// <summary>Kontrastverhältnis nach WCAG 2.x (1–21).</summary>
    public static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double l1 = L(a), l2 = L(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
