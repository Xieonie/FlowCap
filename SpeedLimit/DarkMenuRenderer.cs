using System.Drawing;
using System.Windows.Forms;

namespace SpeedLimit;

/// <summary>Tray-Menü im Graphit-Look statt im hellen Windows-Standard.</summary>
sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new Colors())
{
    static readonly Color Text = Color.FromArgb(235, 235, 240);
    static readonly Color Disabled = Color.FromArgb(120, 120, 130);

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Häkchen in Neon-Cyan statt grauem Kasten
        var r = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2, e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
        using var bg = new SolidBrush(Color.FromArgb(60, 0, 229, 255));
        e.Graphics.FillRectangle(bg, r);
        using var pen = new Pen(Color.FromArgb(0, 229, 255), 2);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var x = e.ImageRectangle.X;
        var y = e.ImageRectangle.Y;
        e.Graphics.DrawLines(pen, [new(x + 3, y + 8), new(x + 6, y + 11), new(x + 13, y + 4)]);
    }

    sealed class Colors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(27, 27, 35);
        static readonly Color Hover = Color.FromArgb(40, 0, 229, 255);
        static readonly Color Border = Color.FromArgb(55, 55, 66);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.FromArgb(0, 229, 255);
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Back;
        public override Color MenuItemPressedGradientEnd => Back;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Back;
        public override Color CheckPressedBackground => Back;
    }
}
