using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SpeedLimit;

/// <summary>Tacho-Bogen mit Download→Upload-Farbverlauf und Gesamtrate in der Mitte.</summary>
public sealed class Gauge : FrameworkElement
{
    const double Sweep = 240, Thickness = 8;
    static Pen TrackPen = null!, ValuePen = null!;
    static Brush CaptionBrush = null!, TextBrush = null!;

    static Gauge() => SetTheme(Theme.All[0]);

    public static void SetTheme(Theme t)
    {
        static Pen Round(Brush b) => RateGraph.Frozen(new Pen(RateGraph.Frozen(b), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        TrackPen = Round(new SolidColorBrush(Theme.WithAlpha(t.Ink, 0x30)));
        ValuePen = Round(new LinearGradientBrush(t.Down, t.Up, 0));
        CaptionBrush = RateGraph.Frozen(new SolidColorBrush(Theme.WithAlpha(t.Ink, 0x99)));
        TextBrush = RateGraph.Frozen(new SolidColorBrush(t.Ink));
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Gauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Gauge), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Gauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(Gauge), new FrameworkPropertyMetadata("gesamt", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>KB/s (bzw. beliebige Einheit, wenn <see cref="Label"/> gesetzt ist).</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>Text in der Mitte; null = Value als Rate formatiert.</summary>
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight * 1.25) / 2 - Thickness;
        if (r < 5) return;
        var center = new Point(ActualWidth / 2, r + Thickness);
        dc.DrawGeometry(null, TrackPen, Arc(center, r, 1));
        var share = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (share > 0.005) dc.DrawGeometry(null, ValuePen, Arc(center, r, share));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var size = Math.Clamp(r / 2.75, 14, 34); // großer Tacho im Speedtest, kleiner unter dem Graphen
        var value = new FormattedText(Label ?? AppRow.FormatRate(Value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, TextBrush, dpi);
        var caption = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.6, CaptionBrush, dpi);
        dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y - value.Height / 2 - caption.Height));
        dc.DrawText(value, new Point(center.X - value.Width / 2, center.Y - value.Height / 2 + 2));
    }

    // Bogen von links unten (-210°) im Uhrzeigersinn über oben, share = Anteil am vollen Bogen
    static Geometry Arc(Point c, double r, double share)
    {
        static Point On(Point c, double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        const double start = 150; // WPF-Winkel: 0° = rechts, im Uhrzeigersinn
        var sweep = Sweep * share;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(On(c, r, start), false, false);
            ctx.ArcTo(On(c, r, start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
