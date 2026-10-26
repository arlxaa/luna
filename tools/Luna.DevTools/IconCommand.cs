using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Luna.UI;

namespace Luna.DevTools;

/// <summary>Draws the glowing-moon app icon and writes a multi-size .ico with PNG frames.</summary>
internal static class IconCommand
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var frames = new List<byte[]>();
        foreach (var n in Sizes)
        {
            using var bmp = n <= 24 ? Small(n) : Detailed(n);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add(ms.ToArray());
            if (n == 256) File.WriteAllBytes(Path.Combine(dir, "luna-256.png"), ms.ToArray());
        }

        using var o = new BinaryWriter(File.Create(Path.Combine(dir, "luna.ico")));
        o.Write((ushort)0);
        o.Write((ushort)1);
        o.Write((ushort)Sizes.Length);
        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
            o.Write(dim); o.Write(dim);
            o.Write((byte)0); o.Write((byte)0);
            o.Write((ushort)1); o.Write((ushort)32);
            o.Write(frames[i].Length);
            o.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) o.Write(f);
        Console.WriteLine($"Wrote {Path.Combine(dir, "luna.ico")}");
        return 0;
    }

    static Bitmap Detailed(int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float f = n, c = f / 2, r = 0.39f * f;

        using (var halo = new GraphicsPath())
        {
            halo.AddEllipse(0, 0, f, f);
            using var pg = new PathGradientBrush(halo)
            {
                CenterColor = Color.FromArgb(190, 140, 186, 255),
                SurroundColors = new[] { Color.FromArgb(0, 140, 186, 255) },
                FocusScales = new PointF(r / c, r / c),
            };
            g.FillPath(pg, halo);
        }

        var disc = new RectangleF(c - r, c - r, 2 * r, 2 * r);
        using (var tex = new TextureBrush(Moon.Get(n).Lit, WrapMode.Clamp)) g.FillEllipse(tex, disc);
        using (var light = new GraphicsPath())
        {
            light.AddEllipse(disc);
            using var pg = new PathGradientBrush(light)
            {
                CenterPoint = new PointF(c - r * 0.25f, c + r * 0.3f),
                CenterColor = Color.FromArgb(100, 255, 255, 255),
                SurroundColors = new[] { Color.FromArgb(70, 28, 58, 122) },
            };
            g.FillPath(pg, light);
        }
        using (var rim = new Pen(Color.FromArgb(230, 238, 246, 255), Math.Max(1f, f / 110))) g.DrawEllipse(rim, disc);

        using var star = new Pen(Color.FromArgb(230, 210, 228, 255), Math.Max(1f, f / 128));
        foreach (var (x, y, len) in new[] { (0.86f, 0.14f, 0.06f), (0.12f, 0.80f, 0.04f) })
        {
            g.DrawLine(star, (x - len) * f, y * f, (x + len) * f, y * f);
            g.DrawLine(star, x * f, (y - len) * f, x * f, (y + len) * f);
        }
        return bmp;
    }

    /// <summary>Tray sizes: a crisp pale disc with a blue edge and two maria, no texture noise.</summary>
    static Bitmap Small(int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float f = n, pad = f * 0.06f;
        var disc = new RectangleF(pad, pad, f - 2 * pad, f - 2 * pad);
        using (var b = new LinearGradientBrush(disc, Color.FromArgb(244, 248, 255), Color.FromArgb(188, 206, 236), 60f)) g.FillEllipse(b, disc);
        using (var mare = new SolidBrush(Color.FromArgb(150, 128, 154, 204)))
        {
            g.FillEllipse(mare, f * 0.30f, f * 0.28f, f * 0.26f, f * 0.20f);
            g.FillEllipse(mare, f * 0.52f, f * 0.55f, f * 0.18f, f * 0.16f);
        }
        using (var edge = new Pen(Color.FromArgb(58, 92, 160), Math.Max(1f, f / 16))) g.DrawEllipse(edge, disc);
        return bmp;
    }
}
