using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MuseDesktop;

/// <summary>
/// An Opera GX-flavoured button: near-black fill, chamfered corners
/// (top-right and bottom-left cut, the GX angular signature), and a
/// neon-red border that lights up on hover. Icons are Segoe MDL2
/// Assets glyphs — the icon font Windows itself ships, so they render
/// at exact size instead of the oversized/cropped symbol-font glyphs
/// the v2.1/v2.2 bars suffered from.
/// </summary>
public sealed class GxButton : Button
{
    public static readonly Color GxRed = Color.FromArgb(250, 30, 78);
    private static readonly Color FillNormal = Color.FromArgb(23, 23, 34);
    private static readonly Color FillHover = Color.FromArgb(42, 30, 44);
    private static readonly Color FillDisabled = Color.FromArgb(16, 16, 22);
    private static readonly Color BorderNormal = Color.FromArgb(52, 52, 72);
    private static readonly Color TextNormal = Color.FromArgb(235, 235, 245);
    private static readonly Color TextDisabled = Color.FromArgb(88, 88, 108);

    private bool _hover;

    public GxButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        TabStop = false;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor,
                 true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var path = ChamferPath(new Rectangle(0, 0, Width, Height));
        Region = new Region(path);
    }

    // Rectangle with the top-right and bottom-left corners cut diagonally.
    private static GraphicsPath ChamferPath(Rectangle r, int cut = 12)
    {
        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new Point(r.Left, r.Top),
            new Point(r.Right - cut, r.Top),
            new Point(r.Right, r.Top + cut),
            new Point(r.Right, r.Bottom),
            new Point(r.Left + cut, r.Bottom),
            new Point(r.Left, r.Bottom - cut),
        });
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = ChamferPath(rect))
        {
            var fill = !Enabled ? FillDisabled : _hover ? FillHover : FillNormal;
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            var borderColor = !Enabled ? BorderNormal : _hover ? GxRed : BorderNormal;
            using var pen = new Pen(borderColor, _hover && Enabled ? 1.6f : 1f);
            g.DrawPath(pen, path);
        }

        var textColor = Enabled ? TextNormal : TextDisabled;
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, 0, Width, Height), textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }
}
