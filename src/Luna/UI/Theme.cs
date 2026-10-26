using System.Drawing.Drawing2D;

namespace Luna.UI;

internal sealed record Theme(
    Color Back, Color Surface, Color Text, Color SubText, Color Border, Color Separator,
    Color Accent, Color AccentText, Color Disabled, Color Danger)
{
    /// <summary>Night sky from the moon artwork: deep navy card, moonlight-blue accent.</summary>
    public static readonly Theme Night = new(
        Back: Color.FromArgb(14, 24, 46), Surface: Color.FromArgb(30, 46, 80),
        Text: Color.FromArgb(236, 242, 255), SubText: Color.FromArgb(158, 177, 212),
        Border: Color.FromArgb(52, 76, 122), Separator: Color.FromArgb(34, 52, 88),
        Accent: Color.FromArgb(143, 184, 255), AccentText: Color.FromArgb(10, 20, 44),
        Disabled: Color.FromArgb(92, 108, 140), Danger: Color.FromArgb(255, 128, 128));

    public static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static readonly string UiFont = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static readonly string UiFontSemibold = FontExists("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";

    static bool FontExists(string name)
    {
        using var f = new Font(name, 10);
        return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class Draw
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using var path = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }
}
