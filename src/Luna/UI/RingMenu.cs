using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Luna.Engines;
using Luna.Ops;

namespace Luna.UI;

/// <summary>
/// The ring menu: a glowing moon cut into slices around a night-sky center. The hovered slice turns
/// navy and lifts; the center pill names it. Drawn with per-pixel alpha, so everything outside the
/// circle is click-through.
///
/// Drag mode: during a Shift-drag an invisible drop zone rides under the cursor; once files enter
/// it the ring appears centered on the cursor and the file is dropped onto a slice.
/// Click mode: from the right-click verb (or a drop on the center); mouse, arrows, Enter, Tab, Esc.
/// With more than 8 options there are two rings, Formats and Tools, switched with Alt while
/// dragging (like Option on the Mac) or Tab / clicking the center.
/// </summary>
internal sealed class RingMenu : Form
{
    enum Mode { Hidden, Drag, Click }

    const int WM_DPICHANGED = 0x02E0;
    const int GhostSize = 72, SinglePageMax = 8;
    const int NoHit = -1, CenterHit = -2;
    const int MK_ALT = 32;

    // Moon palette: glowing lunar surface, blue halo, navy selection like the headphone ear cup,
    // and a night-sky center.
    static readonly Color Halo = Color.FromArgb(140, 186, 255);
    static readonly Color Star = Color.FromArgb(205, 224, 255);
    static readonly Color Rim = Color.FromArgb(225, 238, 246, 255);
    static readonly Color SliceText = Color.FromArgb(27, 50, 98);
    static readonly Color SliceTextOff = Color.FromArgb(105, 27, 50, 98);
    static readonly Color HotTop = Color.FromArgb(60, 102, 184), HotBottom = Color.FromArgb(20, 40, 92);
    static readonly Color HotEdge = Color.FromArgb(210, 168, 204, 255);
    static readonly Color DangerTop = Color.FromArgb(222, 84, 92), DangerBottom = Color.FromArgb(150, 30, 46);
    static readonly Color DangerEdge = Color.FromArgb(215, 255, 190, 196);
    static readonly Color SkyCenter = Color.FromArgb(24, 44, 88), SkyEdge = Color.FromArgb(6, 12, 30);
    static readonly Color Pill = Color.FromArgb(34, 64, 124);
    static readonly Color PillEdge = Color.FromArgb(200, 190, 212, 246);
    static readonly Color PillText = Color.FromArgb(238, 244, 255);
    static readonly Color Caption = Color.FromArgb(155, 182, 226);

    public event Action<Act, IReadOnlyList<string>>? ActionChosen;

    Mode mode = Mode.Hidden;
    bool ghost;
    MenuModel? model;
    string[]? dragFiles;
    readonly List<List<Act>> pages = new();
    int pageIndex, hover = NoHit;

    float s = 1;
    int side;
    float rOut, rIn, rim, discR;
    Point center; // screen coordinates of the ring center

    // Animations: a springy pop-in, the hovered slice lifting outward, and a fade on close.
    const float AppearMs = 280, LiftMs = 110, VanishMs = 140;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
    DateTime animStart, liftStart, closeStart;
    float appear = 1, lift = 1, vanish;
    bool closing;

    Font? formatFont, toolFont, iconFont, pillFont, captionFont;
    float fontScale = -1;

    public bool InDragMode => mode == Mode.Drag && Visible;
    public bool IsOpen => mode != Mode.Hidden && Visible;
    public DateTime LastDrop { get; private set; }

    List<Act> Page => pages.Count == 0 ? new() : pages[pageIndex];
    bool TwoPages => pages.Count > 1;

    public RingMenu()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AllowDrop = true;
        KeyPreview = true;
        Text = "Luna";
        anim.Tick += (_, _) => Animate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; } // we size ourselves per monitor
        base.WndProc(ref m);
    }

    /// <summary>Renders once off-screen so the first real menu appears without JIT delay.</summary>
    public void Warm()
    {
        _ = Handle;
        float scale = Native.DpiScaleAt(Cursor.Position);
        PrepareFonts(scale);
        SetModel(Catalog.Build(new[] { Path.Combine(Path.GetTempPath(), "warm.png") }));
        using (PaintRing()) { }
        model = null;
        pages.Clear();
        // Bake the moon textures for every ring size at this DPI so no drag waits on them.
        Task.Run(() =>
        {
            foreach (var r in RingRadii) Moon.Get(RingSide(r, scale));
        });
    }

    /// <summary>Renders the ring off-screen without showing a window (README screenshots).</summary>
    internal Bitmap RenderPreview(IReadOnlyList<string> files, int page, int hoverIndex, float scale)
    {
        PrepareFonts(scale);
        mode = Mode.Drag;
        SetModel(Catalog.Build(files));
        pageIndex = Math.Min(page, pages.Count - 1);
        hover = hoverIndex;
        appear = lift = 1;
        vanish = 0;
        var bmp = PaintRing();
        mode = Mode.Hidden;
        model = null;
        pages.Clear();
        hover = NoHit;
        return bmp;
    }

    // ---------- showing ----------

    public void ShowForDrag(Point cursor)
    {
        StopClosing();
        model = null;
        dragFiles = null;
        pages.Clear();
        hover = NoHit;
        mode = Mode.Drag;
        ghost = true;
        center = cursor;
        Native.SetNoActivate(Handle, true);
        PrepareFonts(Native.DpiScaleAt(cursor));
        Render();
        if (!Visible) Show();
    }

    public void FollowCursor(Point cursor)
    {
        if (!ghost || mode != Mode.Drag || !Visible) return;
        center = cursor;
        Location = new Point(cursor.X - Width / 2, cursor.Y - Height / 2);
    }

    public void ShowForClick(IReadOnlyList<string> files, Point cursor)
    {
        StopClosing();
        mode = Mode.Click;
        ghost = false;
        PrepareFonts(Native.DpiScaleAt(cursor));
        SetModel(Catalog.Build(files));
        Open(cursor);
        Native.SetNoActivate(Handle, false);
        if (!Visible) Show();
        Native.SetForegroundWindow(Handle);
        Activate();
    }

    /// <summary>Called on mouse-up of a Shift-drag that didn't drop on us.</summary>
    public void EndDrag()
    {
        if (mode == Mode.Drag) Dismiss();
    }

    /// <summary>Closes the ring: fades out if it is showing, hides at once if it is the invisible drop zone.</summary>
    public void Dismiss()
    {
        bool fade = Visible && !ghost && mode != Mode.Hidden;
        mode = Mode.Hidden;
        if (!fade) { FinishClose(); return; }
        closing = true;
        closeStart = DateTime.UtcNow;
        anim.Start();
    }

    void FinishClose()
    {
        closing = false;
        vanish = 0;
        ghost = false;
        hover = NoHit;
        model = null;
        anim.Stop();
        Hide();
    }

    void StopClosing()
    {
        closing = false;
        vanish = 0;
    }

    void Animate()
    {
        var now = DateTime.UtcNow;
        appear = Math.Min(1f, (float)(now - animStart).TotalMilliseconds / AppearMs);
        lift = Math.Min(1f, (float)(now - liftStart).TotalMilliseconds / LiftMs);
        if (closing)
        {
            vanish = Math.Min(1f, (float)(now - closeStart).TotalMilliseconds / VanishMs);
            if (vanish >= 1) { FinishClose(); return; }
        }
        Render();
        if (appear >= 1 && lift >= 1 && !closing) anim.Stop();
    }

    /// <summary>Ease-out with a little overshoot, for the springy macOS-style pop.</summary>
    static float BackOut(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        float u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    static float EaseOut(float t) => 1 - (1 - t) * (1 - t) * (1 - t);

    public bool ContainsScreenPoint(Point p)
    {
        if (!Visible || ghost) return false;
        float dx = p.X - center.X, dy = p.Y - center.Y;
        return dx * dx + dy * dy <= (discR + S(4)) * (discR + S(4));
    }

    void SetModel(MenuModel m)
    {
        model = m;
        pages.Clear();
        var all = m.Formats.Concat(m.Tools).ToList();
        if (all.Count <= SinglePageMax || m.Tools.Count == 0 || m.Formats.Count == 0) pages.Add(all);
        else { pages.Add(m.Formats.ToList()); pages.Add(m.Tools.ToList()); }
        pageIndex = 0;
        hover = NoHit;
        LayoutRing();
    }

    /// <summary>Centers the ring on the cursor (pulled inward near screen edges) and animates it in.</summary>
    void Open(Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        int margin = (int)Math.Ceiling(discR) + S(6);
        center = new Point(
            Math.Clamp(cursor.X, area.Left + margin, Math.Max(area.Left + margin, area.Right - margin)),
            Math.Clamp(cursor.Y, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - margin)));
        appear = 0;
        animStart = DateTime.UtcNow;
        Render();
        anim.Start();
    }

    // ---------- geometry ----------

    int S(float v) => (int)Math.Round(v * s);

    void PrepareFonts(float scale)
    {
        s = scale;
        if (Math.Abs(scale - fontScale) < 0.01f && formatFont != null) return;
        fontScale = scale;
        foreach (var f in new[] { formatFont, toolFont, iconFont, pillFont, captionFont }) f?.Dispose();
        formatFont = new Font(Theme.UiFontSemibold, 13f * s, GraphicsUnit.Pixel);
        toolFont = new Font(Theme.UiFontSemibold, 8.6f * s, GraphicsUnit.Pixel);
        iconFont = new Font(Theme.IconFont, 15f * s, GraphicsUnit.Pixel);
        pillFont = new Font(Theme.UiFontSemibold, 11.5f * s, GraphicsUnit.Pixel);
        captionFont = new Font(Theme.UiFontSemibold, 7.6f * s, GraphicsUnit.Pixel);
    }

    void LayoutRing()
    {
        int n = pages.Count == 0 ? 4 : pages.Max(p => p.Count);
        int radius = RingRadii[n <= 4 ? 0 : n <= 6 ? 1 : n <= 8 ? 2 : n <= 10 ? 3 : 4];
        rOut = S(radius);
        rIn = S(52);
        rim = S(9);
        discR = rOut + rim;
        side = RingSide(radius, s);
    }

    /// <summary>Outer slice radius (at 100%) by slice count: up to 4, 6, 8, 10, more.</summary>
    static readonly int[] RingRadii = { 112, 122, 132, 144, 156 };

    static int RingSide(int radius, float scale) =>
        2 * ((int)Math.Round(radius * scale) + (int)Math.Round(9 * scale) + (int)Math.Round(20 * scale));

    int HitTest(Point client)
    {
        float dx = client.X - side / 2f, dy = client.Y - side / 2f;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < rIn) return CenterHit;
        int n = Page.Count;
        if (n == 0 || d > side / 2f) return NoHit;
        // Pie-menu rule: direction picks the slice, even past the outer edge.
        float step = 360f / n;
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        float rel = ((angle + 90f + step / 2f) % 360f + 360f) % 360f;
        return Math.Min(n - 1, (int)(rel / step));
    }

    // ---------- painting ----------

    void Render()
    {
        if (!IsHandleCreated) return;
        if (ghost)
        {
            // Alpha 1/255: invisible, yet still a hit-testable drop target.
            using var g = new Bitmap(S(GhostSize), S(GhostSize), PixelFormat.Format32bppArgb);
            using (var gr = Graphics.FromImage(g)) gr.Clear(Color.FromArgb(1, 0, 0, 0));
            Native.PushLayered(Handle, g, new Point(center.X - g.Width / 2, center.Y - g.Height / 2), 255);
        }
        else
        {
            using var bmp = PaintRing();
            float alpha = Math.Min(1f, appear * 3f) * (1 - vanish);
            Native.PushLayered(Handle, bmp, new Point(center.X - side / 2, center.Y - side / 2), (byte)(255 * Math.Clamp(alpha, 0, 1)));
        }
        UpdateBounds(); // UpdateLayeredWindow moved/resized us behind WinForms' back
    }

    Bitmap PaintRing()
    {
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var c = new PointF(side / 2f, side / 2f);
        float k = (0.8f + 0.2f * BackOut(appear)) * (1 - 0.06f * vanish);
        g.TranslateTransform(c.X, c.Y);
        g.ScaleTransform(k, k);
        g.TranslateTransform(-c.X, -c.Y);

        DrawGlow(g, c);

        // The moon: shadowed terrain shows in the grooves, lit terrain on the slices. Both textures
        // come from the same heightmap and are aligned to the bitmap, so the surface is continuous.
        var moon = Moon.Get(side);
        using var shadowed = new TextureBrush(moon.Shadow, WrapMode.Clamp);
        using var lit = new TextureBrush(moon.Lit, WrapMode.Clamp);
        var disc = Circle(c, discR);
        g.FillEllipse(shadowed, disc);

        var items = Page;
        int n = items.Count;
        float step = 360f / Math.Max(1, n);
        float gap = 5 * s, corner = 9 * s;
        GraphicsPath Slice(int i) => n == 1 ? Annulus(c, rIn, rOut)
            : SlicePath(c, rIn, rOut, -90 + i * step - step / 2, -90 + i * step + step / 2, gap, corner);

        for (int i = 0; i < n; i++)
        {
            if (i == hover) continue;
            using var path = Slice(i);
            FillRounded(g, path, lit, n > 1 ? corner : 0);
        }

        // Moonlight: brightest toward the lower left as in the artwork, blue at the limb.
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(disc);
            using var pg = new PathGradientBrush(path)
            {
                CenterPoint = new PointF(c.X - discR * 0.25f, c.Y + discR * 0.3f),
                CenterColor = Color.FromArgb(95, 255, 255, 255),
                SurroundColors = new[] { Color.FromArgb(48, 28, 58, 122) },
            };
            g.FillPath(pg, path);
        }
        using (var p = new Pen(Rim, Math.Max(1f, 1.3f * s))) g.DrawEllipse(p, disc);

        // The hovered slice lifts outward a little.
        float liftAngle = (-90 + hover * step) * MathF.PI / 180f, liftBy = n > 1 ? S(6) * EaseOut(lift) : 0;
        var liftOffset = new SizeF(liftBy * MathF.Cos(liftAngle), liftBy * MathF.Sin(liftAngle));
        if (hover >= 0 && hover < n)
        {
            var state = g.Save();
            g.TranslateTransform(liftOffset.Width, liftOffset.Height);
            using var path = Slice(hover);
            var bounds = path.GetBounds();
            bounds.Inflate(corner + 1, corner + 1);
            // A pale drawn edge (like the line art in the picture): a slightly wider rounded
            // stroke underneath, then the navy slice on top.
            // Destructive actions (Delete) light up red instead of navy.
            bool danger = items[hover].Destructive;
            if (n > 1)
                using (var edge = new Pen(danger ? DangerEdge : HotEdge, corner * 2 + Math.Max(2f, 2.4f * s)) { LineJoin = LineJoin.Round })
                    g.DrawPath(edge, path);
            using var fill = new LinearGradientBrush(bounds, danger ? DangerTop : HotTop, danger ? DangerBottom : HotBottom, LinearGradientMode.Vertical);
            FillRounded(g, path, fill, n > 1 ? corner : 0);
            g.Restore(state);
        }

        for (int i = 0; i < n; i++)
        {
            bool hot = i == hover;
            DrawLabel(g, items[i], hot ? c + liftOffset : c, -90 + i * step, step, n, hot);
        }

        DrawCenter(g, c);
        return bmp;
    }

    static void FillRounded(Graphics g, GraphicsPath inset, Brush brush, float corner)
    {
        g.FillPath(brush, inset);
        if (corner > 0)
            using (var round = new Pen(brush, corner * 2) { LineJoin = LineJoin.Round })
                g.DrawPath(round, inset);
    }

    /// <summary>Blue halo, a reflection pool below and scattered stars, like the artwork.</summary>
    void DrawGlow(Graphics g, PointF c)
    {
        float gr = discR + S(19);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(Circle(c, gr));
            float focus = discR / gr;
            using var pg = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(165, Halo),
                SurroundColors = new[] { Color.FromArgb(0, Halo) },
                FocusScales = new PointF(focus, focus),
            };
            g.FillPath(pg, path);
        }

        var pool = new RectangleF(c.X - discR * 0.75f, c.Y + discR + S(9), discR * 1.5f, S(7));
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(pool);
            using var pg = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(200, 175, 212, 255),
                SurroundColors = new[] { Color.FromArgb(0, 175, 212, 255) },
            };
            g.FillPath(pg, path);
        }

        var rng = new Random(11);
        using var star = new SolidBrush(Star);
        using var sparkle = new Pen(Star, Math.Max(0.8f, 0.9f * s));
        for (int i = 0; i < 18; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2;
            float r = discR + S(4) + (float)rng.NextDouble() * S(11);
            var p = new PointF(c.X + r * (float)Math.Cos(a), c.Y + r * (float)Math.Sin(a));
            float size = (0.6f + (float)rng.NextDouble() * 1.0f) * s;
            if (i % 5 == 0)
            {
                g.DrawLine(sparkle, p.X - size * 2.2f, p.Y, p.X + size * 2.2f, p.Y);
                g.DrawLine(sparkle, p.X, p.Y - size * 2.2f, p.X, p.Y + size * 2.2f);
            }
            else g.FillEllipse(star, p.X - size, p.Y - size, size * 2, size * 2);
        }
    }

    void DrawLabel(Graphics g, Act act, PointF c, float mid, float step, int n, bool hot)
    {
        float rm = (rIn + rOut) / 2 + (n == 1 ? 0 : S(2));
        float a = mid * MathF.PI / 180f;
        var pos = new PointF(c.X + rm * MathF.Cos(a), c.Y + rm * MathF.Sin(a));
        float maxW = n >= 3 ? 2 * rm * MathF.Sin(step * MathF.PI / 360f) - S(16) : (rOut - rIn) * 1.5f;
        bool available = Tools.Has(act.Needs);
        var color = hot ? Color.White : available ? SliceText : SliceTextOff;

        if (act.IsFormat)
        {
            DrawFitted(g, act.Short, formatFont!, color, pos, maxW);
            return;
        }
        using (var brush = new SolidBrush(color))
            DrawCentered(g, act.Glyph, iconFont!, brush, new PointF(pos.X, pos.Y - S(8)));
        DrawFitted(g, act.Short, toolFont!, color, new PointF(pos.X, pos.Y + S(10)), maxW);
    }

    void DrawCenter(Graphics g, PointF c)
    {
        // A patch of night sky with a few stars.
        float hr = rIn - S(5);
        var hole = Circle(c, hr);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(hole);
            using var pg = new PathGradientBrush(path) { CenterColor = SkyCenter, SurroundColors = new[] { SkyEdge } };
            g.FillPath(pg, path);
        }
        var rng = new Random(23);
        for (int i = 0; i < 10; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2;
            float r = (float)rng.NextDouble() * (hr - S(4));
            float dot = (0.4f + (float)rng.NextDouble() * 0.8f) * s;
            using var b = new SolidBrush(Color.FromArgb(110 + rng.Next(130), Star));
            g.FillEllipse(b, c.X + r * (float)Math.Cos(a) - dot, c.Y + r * (float)Math.Sin(a) - dot, dot * 2, dot * 2);
        }
        using (var p = new Pen(Color.FromArgb(170, 190, 212, 246), Math.Max(1f, s))) g.DrawEllipse(p, hole);

        string caption = TwoPages
            ? (mode == Mode.Click ? "TAB" : "ALT") + (pageIndex == 0 ? " · TOOLS" : " · FORMATS")
            : "";
        string text = PillLabel();
        var size = g.MeasureString(text, pillFont!);
        float ph = S(30);
        float pw = Math.Clamp(size.Width + S(20), S(62), hr * 2 - S(8));
        float py = c.Y - ph / 2 - (caption.Length > 0 ? S(6) : 0);
        var pill = new RectangleF(c.X - pw / 2, py, pw, ph);
        bool hot = hover == CenterHit && mode == Mode.Click && TwoPages;
        bool danger = hover >= 0 && hover < Page.Count && Page[hover].Destructive;
        // The pill echoes the headphone ear cup: navy with a pale drawn outline (red for Delete).
        using (var path = Draw.Round(pill, ph / 2))
        {
            using (Brush b = danger ? new LinearGradientBrush(pill, DangerTop, DangerBottom, LinearGradientMode.Vertical)
                           : hot ? new LinearGradientBrush(pill, Color.FromArgb(92, 140, 222), Color.FromArgb(48, 88, 170), LinearGradientMode.Vertical)
                           : new SolidBrush(Pill))
                g.FillPath(b, path);
            using var edge = new Pen(PillEdge, Math.Max(1f, 1.1f * s));
            g.DrawPath(edge, path);
        }
        DrawFitted(g, text, pillFont!, PillText, new PointF(c.X, py + ph / 2), pw - S(12));

        if (caption.Length > 0)
            DrawFitted(g, caption, captionFont!, Caption, new PointF(c.X, py + ph + S(9)), hr * 2 - S(14));
    }

    string PillLabel()
    {
        if (hover >= 0 && hover < Page.Count)
        {
            var act = Page[hover];
            var missing = Tools.Missing(act.Needs);
            if (missing == Need.None) return act.Short;
            return missing is Need.Word or Need.Excel or Need.PowerPoint ? "NEEDS OFFICE" : $"GET {Tools.Describe(missing).ToUpperInvariant()}";
        }
        if (hover == CenterHit && TwoPages && mode == Mode.Click) return pageIndex == 0 ? "TOOLS ›" : "‹ FORMATS";
        if (model == null) return "";
        if (model.Files.Count > 1) return $"{model.Files.Count} FILES";
        var f = model.Files[0];
        return File.Exists(f) ? ImageOps.Size(new FileInfo(f).Length).ToUpperInvariant() : "FOLDER";
    }

    static void DrawCentered(Graphics g, string text, Font font, Brush brush, PointF at)
    {
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, at, sf);
    }

    /// <summary>Draws centered text, shrinking it to fit <paramref name="maxWidth"/>.</summary>
    static void DrawFitted(Graphics g, string text, Font font, Color color, PointF at, float maxWidth)
    {
        var width = g.MeasureString(text, font).Width;
        float f = width > maxWidth && maxWidth > 0 ? Math.Max(0.6f, maxWidth / width) : 1f;
        var state = g.Save();
        g.TranslateTransform(at.X, at.Y);
        g.ScaleTransform(f, f);
        using (var b = new SolidBrush(color)) DrawCentered(g, text, font, b, PointF.Empty);
        g.Restore(state);
    }

    static RectangleF Circle(PointF c, float r) => new(c.X - r, c.Y - r, r * 2, r * 2);

    static GraphicsPath Annulus(PointF c, float rIn, float rOut)
    {
        var p = new GraphicsPath(FillMode.Alternate);
        p.AddEllipse(Circle(c, rOut));
        p.AddEllipse(Circle(c, rIn));
        return p;
    }

    /// <summary>
    /// A ring slice inset by the corner radius; filling it and stroking it with a round-joined pen
    /// of twice that width gives the full-size slice with rounded corners and a constant-width gap.
    /// </summary>
    static GraphicsPath SlicePath(PointF c, float rIn, float rOut, float a0, float a1, float gap, float corner)
    {
        float ro = rOut - corner, ri = rIn + corner, half = gap / 2 + corner;
        float dOut = MathF.Asin(Math.Min(1f, half / ro)) * 180f / MathF.PI;
        float dIn = MathF.Asin(Math.Min(1f, half / ri)) * 180f / MathF.PI;
        float o0 = a0 + dOut, o1 = a1 - dOut, i0 = a0 + dIn, i1 = a1 - dIn;
        if (i1 < i0) i0 = i1 = (a0 + a1) / 2;
        var p = new GraphicsPath();
        p.AddArc(c.X - ro, c.Y - ro, 2 * ro, 2 * ro, o0, Math.Max(0.1f, o1 - o0));
        p.AddArc(c.X - ri, c.Y - ri, 2 * ri, 2 * ri, i1, -Math.Max(0.1f, i1 - i0));
        p.CloseFigure();
        return p;
    }

    // ---------- choosing ----------

    void SetHover(int h)
    {
        if (h == hover || closing) return;
        hover = h;
        if (h >= 0)
        {
            lift = 0;
            liftStart = DateTime.UtcNow;
            anim.Start();
        }
        Render();
    }

    void SwitchPage(int index)
    {
        if (!TwoPages || index == pageIndex) return;
        pageIndex = index;
        hover = NoHit;
        Render();
    }

    void Choose(Act act)
    {
        var files = model!.Files;
        Dismiss();
        ActionChosen?.Invoke(act, files);
    }

    // ---------- drag & drop ----------

    static string[]? FilesOf(DragEventArgs e) =>
        e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    static DragDropEffects SafeEffect(DragEventArgs e) =>
        // Never answer Move: the source would delete the original file after the drop.
        e.AllowedEffect.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : e.AllowedEffect.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.None;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        var files = FilesOf(e);
        if (files == null || files.Length == 0 || closing) { e.Effect = DragDropEffects.None; return; }
        if (mode == Mode.Hidden) mode = Mode.Drag;
        if (model == null || dragFiles == null || !dragFiles.SequenceEqual(files))
        {
            dragFiles = files;
            SetModel(Catalog.Build(files));
            if (ghost)
            {
                ghost = false;
                Open(new Point(e.X, e.Y));
            }
            else Render();
        }
        UpdateDrag(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (FilesOf(e) == null) { e.Effect = DragDropEffects.None; return; }
        UpdateDrag(e);
    }

    void UpdateDrag(DragEventArgs e)
    {
        if (TwoPages) SwitchPage((e.KeyState & MK_ALT) != 0 ? 1 : 0);
        SetHover(ghost ? NoHit : HitTest(PointToClient(new Point(e.X, e.Y))));
        e.Effect = SafeEffect(e);
        e.DropImageType = e.Effect == DragDropEffects.Copy ? DropImageType.Copy : DropImageType.Link;
        if (hover >= 0)
        {
            var act = Page[hover];
            e.Message = act.IsFormat ? "Convert to %1" : "%1";
            e.MessageReplacementToken = act.Label;
        }
        else
        {
            e.Message = TwoPages ? (pageIndex == 0 ? "Pick a format · Alt for tools" : "Pick a tool") : "Pick an option";
            e.MessageReplacementToken = "";
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        SetHover(NoHit);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        LastDrop = DateTime.UtcNow;
        if (FilesOf(e) is not { Length: > 0 } || model == null) { Dismiss(); return; }
        int hit = HitTest(PointToClient(new Point(e.X, e.Y)));
        if (hit >= 0) { Choose(Page[hit]); return; }
        // Dropped in the middle: keep the ring open and let the user click.
        mode = Mode.Click;
        Native.SetNoActivate(Handle, false);
        Activate();
        Render();
    }

    // ---------- click mode ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (mode == Mode.Click) SetHover(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (mode == Mode.Click) SetHover(NoHit);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (mode != Mode.Click || e.Button != MouseButtons.Left) return;
        int hit = HitTest(e.Location);
        if (hit >= 0) Choose(Page[hit]);
        else if (hit == CenterHit) SwitchPage(1 - pageIndex);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (mode == Mode.Click && model != null)
        {
            int n = Page.Count;
            switch (keyData)
            {
                case Keys.Escape: Dismiss(); return true;
                case Keys.Tab: case Keys.Shift | Keys.Tab: SwitchPage(1 - pageIndex); return true;
                case Keys.Right: case Keys.Down: SetHover(hover < 0 ? 0 : (hover + 1) % n); return true;
                case Keys.Left: case Keys.Up: SetHover(hover < 0 ? n - 1 : (hover - 1 + n) % n); return true;
                case Keys.Enter: case Keys.Space:
                    if (hover >= 0) Choose(Page[hover]);
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (mode == Mode.Click) Dismiss();
    }
}
