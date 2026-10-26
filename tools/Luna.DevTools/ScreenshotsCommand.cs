using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Luna.UI;

namespace Luna.DevTools;

/// <summary>
/// Renders the README images off-screen onto a starry night background, so screenshots never
/// contain anything from the developer's desktop.
/// </summary>
internal static class ScreenshotsCommand
{
    const float Scale = 1.5f;

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var samples = Path.Combine(Path.GetTempPath(), "luna-shots");
        Directory.CreateDirectory(samples);
        string Sample(string name, int kb)
        {
            var path = Path.Combine(samples, name);
            File.WriteAllBytes(path, new byte[kb * 1024]);
            return path;
        }

        var photo = Sample("photo.heic", 2458);
        var pdf = Sample("report.pdf", 845);
        var video = Sample("clip.mov", 18_400);

        using var ring = new RingMenu();
        using (var formats = ring.RenderPreview(new[] { photo }, page: 0, hoverIndex: 2, Scale))
        using (var tools = ring.RenderPreview(new[] { photo }, page: 1, hoverIndex: 0, Scale))
        using (var docs = ring.RenderPreview(new[] { pdf }, page: 0, hoverIndex: 0, Scale))
        {
            Save(Compose(new[] { formats, tools, docs }, gap: 10), Path.Combine(dir, "rings.png"));
            Save(Compose(new[] { formats }, gap: 0), Path.Combine(dir, "ring-formats.png"));
            Save(Compose(new[] { tools }, gap: 0), Path.Combine(dir, "ring-tools.png"));
        }
        using (var media = ring.RenderPreview(new[] { video }, page: 0, hoverIndex: 0, Scale))
            Save(Compose(new[] { media }, gap: 0), Path.Combine(dir, "ring-video.png"));

        using var toast = new Toast();
        using (var progress = toast.RenderPreview("Converting to MP4…", "2 of 3  ·  clip.mov", "", "Cancel", 0.42, Scale))
        using (var saved = toast.RenderPreview("Saved", "photo.jpg  ·  2.4 MB → 412 KB (83% smaller)", "", "Show", -1, Scale))
        using (var qr = toast.RenderPreview("QR code copied", "https://github.com/arlxaa/luna", "", "Open", -1, Scale))
        using (var recycled = toast.RenderPreview("Moved to Recycle Bin", "3 items", "", "Undo", -1, Scale))
            Save(Stack(new[] { progress, saved, qr, recycled }), Path.Combine(dir, "notifications.png"));

        Directory.Delete(samples, true);
        Console.WriteLine($"Wrote screenshots to {dir}");
        return 0;
    }

    static Bitmap Compose(Bitmap[] parts, int gap)
    {
        int pad = 24;
        int w = parts.Sum(p => p.Width) + gap * (parts.Length - 1) + pad * 2;
        int h = parts.Max(p => p.Height) + pad * 2;
        var canvas = NightSky(w, h);
        using var g = Graphics.FromImage(canvas);
        int x = pad;
        foreach (var p in parts)
        {
            g.DrawImage(p, x, pad + (h - 2 * pad - p.Height) / 2);
            x += p.Width + gap;
        }
        return canvas;
    }

    static Bitmap Stack(Bitmap[] parts)
    {
        int pad = 28;
        int w = parts.Max(p => p.Width) + pad * 2;
        int h = parts.Sum(p => p.Height) + pad * 2 - 16 * (parts.Length - 1);
        var canvas = NightSky(w, h);
        using var g = Graphics.FromImage(canvas);
        int y = pad;
        foreach (var p in parts)
        {
            g.DrawImage(p, pad, y);
            y += p.Height - 16;
        }
        return canvas;
    }

    static Bitmap NightSky(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var sky = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(14, 22, 44), Color.FromArgb(4, 7, 16), 90f))
            g.FillRectangle(sky, 0, 0, w, h);
        var rng = new Random(5);
        for (int i = 0; i < w * h / 2600; i++)
        {
            float x = (float)rng.NextDouble() * w, y = (float)rng.NextDouble() * h, r = 0.4f + (float)rng.NextDouble() * 0.9f;
            using var b = new SolidBrush(Color.FromArgb(60 + rng.Next(150), 205, 222, 255));
            g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
        }
        return bmp;
    }

    static void Save(Bitmap bmp, string path)
    {
        using (bmp) bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }
}
