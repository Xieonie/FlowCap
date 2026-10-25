using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SpeedLimit;

/// <summary>Weiches Flächendiagramm: Download neon-cyan, Upload neon-grün; ohne Raster als Sparkline nutzbar.</summary>
public sealed class RateGraph : FrameworkElement
{
    public static readonly Color DownColor = Color.FromRgb(0x00, 0xE5, 0xFF);
    public static readonly Color UpColor = Color.FromRgb(0xA3, 0xFF, 0x12);

    static readonly Brush DownFill = Fill(DownColor), UpFill = Fill(UpColor);
    static readonly Pen DownPen = Frozen(new Pen(Frozen(new SolidColorBrush(DownColor)), 2));
    static readonly Pen UpPen = Frozen(new Pen(Frozen(new SolidColorBrush(UpColor)), 2));
    static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))), 1));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)));
    const double AxisHeight = 22;

    public static readonly DependencyProperty DownProperty = DependencyProperty.Register(
        nameof(Down), typeof(IReadOnlyList<double>), typeof(RateGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpProperty = DependencyProperty.Register(
        nameof(Up), typeof(IReadOnlyList<double>), typeof(RateGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(RateGraph), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>KB/s pro Sekunde, ältester Wert zuerst, letzter Wert = jetzt.</summary>
    public IReadOnlyList<double>? Down { get => (IReadOnlyList<double>?)GetValue(DownProperty); set => SetValue(DownProperty, value); }
    public IReadOnlyList<double>? Up { get => (IReadOnlyList<double>?)GetValue(UpProperty); set => SetValue(UpProperty, value); }
    /// <summary>Raster, Skala und Zeitachse (großer Graph).</summary>
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - (ShowGrid ? AxisHeight : 0);
        if (w < 2 || h < 2) return;
        var down = Down ?? [];
        var up = Up ?? [];
        var peak = down.Concat(up).DefaultIfEmpty(0).Max();
        var max = Math.Max(peak, ShowGrid ? 10 : 1) * 1.2; // etwas Luft nach oben
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight)); // ganze Fläche hit-testbar
        if (ShowGrid)
        {
            for (var i = 0; i <= 3; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }
            dc.DrawText(Text(AppRow.FormatRate(max), dpi), new Point(0, 4));
            // Zeitachse: Uhrzeiten wie bei GlassWire, ältester Punkt links
            var n = Math.Max(down.Count, 2);
            for (var k = 0; k <= 4; k++)
            {
                var label = Text(DateTime.Now.AddSeconds(-(n - 1) * (1 - k / 4.0)).ToString("HH:mm:ss"), dpi);
                var x = Math.Clamp(w * k / 4 - label.Width / 2, 0, w - label.Width);
                dc.DrawText(label, new Point(x, h + 6));
            }
        }
        DrawSeries(dc, up, max, w, h, UpFill, UpPen);
        DrawSeries(dc, down, max, w, h, DownFill, DownPen);
    }

    FormattedText Text(string s, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush, dpi);

    static void DrawSeries(DrawingContext dc, IReadOnlyList<double> data, double max, double w, double h, Brush fill, Pen pen)
    {
        if (data.Count < 2) return;
        var step = w / (data.Count - 1);
        var pts = new Point[data.Count];
        for (var i = 0; i < pts.Length; i++) pts[i] = new(i * step, h - Math.Min(data[i] / max, 1) * (h - 1));

        var area = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
            c.LineTo(pts[0], false, false);
            Smooth(c, pts, h, stroked: false);
            c.LineTo(new Point(w, h), false, false);
        }
        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(pts[0], isFilled: false, isClosed: false);
            Smooth(c, pts, h, stroked: true);
        }
        area.Freeze();
        line.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    // Catmull-Rom → Bézier für weiche Kurven; Kontrollpunkte nicht unter die Nulllinie
    static void Smooth(StreamGeometryContext c, Point[] p, double h, bool stroked)
    {
        for (var i = 0; i < p.Length - 1; i++)
        {
            var p0 = p[Math.Max(i - 1, 0)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(i + 2, p.Length - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Min(h, p1.Y + (p2.Y - p0.Y) / 6));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Min(h, p2.Y - (p3.Y - p1.Y) / 6));
            c.BezierTo(c1, c2, p2, stroked, true);
        }
    }

    static Brush Fill(Color c) => Frozen(new LinearGradientBrush(Color.FromArgb(0xC0, c.R, c.G, c.B), Color.FromArgb(0x10, c.R, c.G, c.B), 90));

    internal static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
