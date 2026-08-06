using System.Drawing;
using System.Windows.Forms;

namespace RavensPort.Tray;

/// <summary>
/// Custom WinForms ToolStrip renderer that paints the tray context menu in the app's
/// dark palette — neutrals match AIUsage's DarkMenuRenderer so the two apps read as one
/// system; the accent is RavensPort's own gold, taken from the tray raven.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color BgColor = Color.FromArgb(0x1A, 0x1A, 0x1A);
    private static readonly Color HoverBg = Color.FromArgb(0x2E, 0x2E, 0x2E);
    private static readonly Color PressedBg = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color TextColor = Color.FromArgb(0xEB, 0xEB, 0xEB);
    private static readonly Color SeparatorColor = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color BorderColor = Color.FromArgb(0x33, 0x33, 0x33);
    private static readonly Color AccentColor = Color.FromArgb(0xEB, 0x99, 0x0A);

    public DarkMenuRenderer() : base(new DarkColorTable())
    {
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BgColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(BorderColor);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var bounds = new Rectangle(2, 0, item.Width - 4, item.Height);

        Color fill;
        if (!item.Enabled) fill = BgColor;
        else if (item.Pressed) fill = PressedBg;
        else if (item.Selected) fill = HoverBg;
        else fill = BgColor;

        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, bounds);

        if (item.Selected && item.Enabled)
        {
            using var accentBrush = new SolidBrush(AccentColor);
            e.Graphics.FillRectangle(accentBrush, new Rectangle(2, 2, 2, item.Height - 4));
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : Color.FromArgb(0x55, 0x55, 0x55);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SeparatorColor);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = TextColor;
        base.OnRenderArrow(e);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => BorderColor;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => HoverBg;
        public override Color MenuItemSelectedGradientBegin => HoverBg;
        public override Color MenuItemSelectedGradientEnd => HoverBg;
        public override Color MenuItemPressedGradientBegin => PressedBg;
        public override Color MenuItemPressedGradientEnd => PressedBg;
        public override Color ToolStripDropDownBackground => BgColor;
        public override Color ImageMarginGradientBegin => BgColor;
        public override Color ImageMarginGradientMiddle => BgColor;
        public override Color ImageMarginGradientEnd => BgColor;
    }
}
