using System.Windows;
using System.Windows.Controls;
using SpeedLimit;

public class DashboardPanelTests
{
    static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    static (DashboardPanel Panel, Dictionary<string, Border> Kids, ScrollViewer Viewer) Build(DashboardLayout layout, double w, double h)
    {
        var panel = new DashboardPanel();
        var kids = layout.Tiles.ToDictionary(t => t.Id, t =>
        {
            var b = new Border { Visibility = t.Hidden ? Visibility.Collapsed : Visibility.Visible };
            DashboardPanel.SetPlacement(b, t);
            panel.Children.Add(b);
            return b;
        });
        var viewer = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        for (var i = 0; i < 3; i++) // ScrollViewer kennt seinen sichtbaren Bereich erst nach einem ersten Durchlauf
        {
            viewer.Measure(new Size(w, h));
            viewer.Arrange(new Rect(0, 0, w, h));
            viewer.UpdateLayout();
        }
        return (panel, kids, viewer);
    }

    static Rect Bounds(FrameworkElement e, FrameworkElement root) => new(e.TranslatePoint(new Point(0, 0), root), e.RenderSize);

    [Fact]
    public void DefaultLayout_FillsViewportExactly() => OnSta(() =>
    {
        var (panel, kids, viewer) = Build(DashboardLayout.Default(), 1200, 700);
        var graph = Bounds(kids["graph"], panel);
        var apps = Bounds(kids["apps"], panel);
        var details = Bounds(kids["details"], panel);
        Assert.Equal(1200, graph.Width, 0.5);                    // volle Breite, keine Scrollleiste nötig
        Assert.Equal(700, details.Bottom, 0.5);                  // unterste Module enden genau am Fensterrand
        Assert.Equal(apps.Bottom, details.Bottom, 0.5);
        Assert.Equal(DashboardPanel.Gap, details.Left - apps.Right, 0.5);
        Assert.Equal(0, viewer.ScrollableHeight, 0.5);
    });

    [Fact]
    public void LayoutTallerThanWindow_BecomesScrollable() => OnSta(() =>
    {
        // 18 Zeilen hoch – mehr als die 12, die ins Fenster passen
        var layout = new DashboardLayout
        {
            Tiles = [new("graph", 0, 0, 12, 10), new("stats", 0, 10, 12, 2), new("apps", 0, 12, 8, 6), new("details", 8, 12, 4, 6)],
        };
        var (_, _, viewer) = Build(layout, 1200, 700);
        Assert.True(viewer.ScrollableHeight > 0);
    });
}
