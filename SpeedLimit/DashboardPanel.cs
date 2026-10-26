using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpeedLimit;

/// <summary>
/// Ordnet Kinder in einem Raster mit <see cref="DashboardLayout.Columns"/> Spalten an. <see cref="DashboardLayout.VisibleRows"/> Zeilen füllen
/// genau den sichtbaren Bereich des umgebenden ScrollViewers; liegen Module tiefer, wird das Panel höher und der ScrollViewer scrollt.
/// Ändert sich die Position eines Kindes, gleitet es dorthin (statt zu springen).
/// </summary>
public sealed class DashboardPanel : Panel
{
    public const double Gap = 14;
    static readonly Duration Glide = TimeSpan.FromMilliseconds(180);

    public static readonly DependencyProperty PlacementProperty = DependencyProperty.RegisterAttached(
        "Placement", typeof(Tile), typeof(DashboardPanel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>Anordnen-Modus; wird an alle Module vererbt (deren Vorlage blendet damit die Werkzeuge ein).</summary>
    public static readonly DependencyProperty EditingProperty = DependencyProperty.RegisterAttached(
        "Editing", typeof(bool), typeof(DashboardPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Wird gerade gezogen: kein Gleiten, die Verschiebung kommt vom Ziehen selbst.</summary>
    public static readonly DependencyProperty IsDraggedProperty = DependencyProperty.RegisterAttached(
        "IsDragged", typeof(bool), typeof(DashboardPanel), new FrameworkPropertyMetadata(false));

    public static void SetPlacement(UIElement e, Tile? t) => e.SetValue(PlacementProperty, t);
    public static Tile? GetPlacement(UIElement e) => (Tile?)e.GetValue(PlacementProperty);
    public static void SetEditing(DependencyObject e, bool v) => e.SetValue(EditingProperty, v);
    public static bool GetEditing(DependencyObject e) => (bool)e.GetValue(EditingProperty);
    public static void SetIsDragged(UIElement e, bool v) => e.SetValue(IsDraggedProperty, v);
    public static bool GetIsDragged(UIElement e) => (bool)e.GetValue(IsDraggedProperty);

    /// <summary>Größe eines Feldes inklusive Abstand – für das Einrasten beim Ziehen.</summary>
    public Size CellSize { get; private set; } = new(1, 1);

    readonly Dictionary<UIElement, Rect> lastSlots = [];
    ScrollViewer? viewer;

    /// <summary>Pixel-Rechteck einer Kachel.</summary>
    public Rect SlotOf(Tile t) => Slot(t, CellSize);

    // Beim Einhängen ist die Vorlage des ScrollViewers oft noch nicht verbunden – deshalb erst beim Ausmessen suchen
    void FindViewer()
    {
        if (viewer is not null) return;
        for (DependencyObject? p = VisualParent; p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) { viewer = sv; break; }
        if (viewer is null) return;
        viewer.SizeChanged += (_, _) => InvalidateMeasure();
        // Die sichtbare Höhe steht erst nach dem ersten Durchlauf fest
        viewer.ScrollChanged += (_, e) => { if (e.ViewportHeightChange != 0) InvalidateMeasure(); };
    }

    static Rect Slot(Tile t, Size cell) =>
        new(t.X * cell.Width, t.Y * cell.Height, Math.Max(0, t.W * cell.Width - Gap), Math.Max(0, t.H * cell.Height - Gap));

    protected override Size MeasureOverride(Size available)
    {
        FindViewer();
        var width = double.IsInfinity(available.Width) ? 0 : available.Width;
        // Zeilenhöhe aus dem sichtbaren Bereich, nicht aus der (im ScrollViewer unendlichen) verfügbaren Höhe
        var viewport = viewer?.ViewportHeight is > 0 ? viewer.ViewportHeight : double.IsInfinity(available.Height) ? 600 : available.Height;
        CellSize = new((width + Gap) / DashboardLayout.Columns, (viewport + Gap) / DashboardLayout.VisibleRows);

        var bottom = DashboardLayout.VisibleRows;
        foreach (UIElement child in InternalChildren)
        {
            var t = GetPlacement(child);
            if (t is not null && child.Visibility == Visibility.Visible) bottom = Math.Max(bottom, t.Y + t.H);
            child.Measure(t is not null ? Slot(t, CellSize).Size : default);
        }
        return new Size(width, bottom * CellSize.Height - Gap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in InternalChildren)
        {
            var slot = GetPlacement(child) is { } t ? Slot(t, CellSize) : default;
            child.Arrange(slot);
            // Gleiten: vom alten an den neuen Platz animieren (nur echte Verschiebungen, nicht beim ersten Anordnen oder Fenster-Ändern)
            if (lastSlots.TryGetValue(child, out var old) && old.Size == slot.Size && old.TopLeft != slot.TopLeft && !GetIsDragged(child))
                GlideFrom(child, old.X - slot.X, old.Y - slot.Y);
            lastSlots[child] = slot;
        }
        return final;
    }

    static void GlideFrom(UIElement child, double dx, double dy)
    {
        if (child.RenderTransform is not TranslateTransform move || move.IsFrozen) child.RenderTransform = move = new TranslateTransform();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, Glide) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Glide) { EasingFunction = ease });
    }
}
