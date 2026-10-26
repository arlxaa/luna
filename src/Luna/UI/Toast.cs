using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Luna.UI;

/// <summary>
/// macOS-style notification banner in the top-right corner: app icon with a status badge, title,
/// "now", up to two lines of text, and a progress bar while a job runs. Hovering reveals a close
/// button on the corner and an action button. It slides in from the right and fades out.
/// Drawn with per-pixel alpha for the large corner radius and soft shadow.
/// </summary>
internal sealed class Toast : Form
{
    const int WM_DPICHANGED = 0x02E0;
    const int BaseWidth = 356, Radius = 18, Shadow = 18;
    const double SlideInMs = 260, FadeOutMs = 180;

    readonly Theme theme = Theme.Night;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 16 };

    Job? job;
    string title = "", body = "", badge = "";
    bool running, error;
    double progress = -1;
    Action? onClick, onButton;
    string? buttonText;

    DateTime hideAt = DateTime.MaxValue, animStart, lastRender;
    float slide = 1;   // 0 → 1 while sliding in
    float fade = 1;    // 1 → 0 while fading out
    bool closing;

    bool hovered, hoverClose, hoverButton;
    Rectangle card, closeRect, buttonRect;
    Point rest; // window top-left when fully in place
    float s = 1;
    Font? titleFont, bodyFont, timeFont, buttonFont, glyphFont;
    Bitmap? icon;
    int pulse;

    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Luna";
        tick.Tick += (_, _) => OnTick();
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
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    // ---------- public API ----------

    public void Track(Job j, int queued)
    {
        job = j;
        running = true;
        error = false;
        onClick = null;
        onButton = () => j.Cts.Cancel();
        buttonText = "Cancel";
        title = j.Act.Verb + "…";
        body = Path.GetFileName(j.Current.TrimEnd('\\')) + (queued > 0 ? $"  ·  {queued} more queued" : "");
        badge = "";
        progress = -1;
        hideAt = DateTime.MaxValue;
        Present();
    }

    public void Finish(Job j)
    {
        job = j;
        running = false;
        progress = -1;
        onClick = null;
        onButton = null;
        buttonText = null;
        switch (j.State)
        {
            case JobState.Canceled:
                Set("Canceled", "Nothing was changed.", "\uE711", false, 4);
                break;
            case JobState.Done when j.Act.Id == "delete":
                Set(j.Undo != null ? "Moved to Recycle Bin" : "Deleted", j.Note ?? "", "\uE74D", false, 10);
                if (j.Undo is { } undo)
                {
                    buttonText = "Undo";
                    onButton = () =>
                    {
                        int n = undo();
                        Message(n > 0 ? "Put back" : "Couldn't put back",
                            n > 0 ? $"{(n == 1 ? j.Note : $"{n} items")} restored to where {(n == 1 ? "it was" : "they were")}."
                                  : "The items are no longer in the Recycle Bin.",
                            n > 0 ? "\uE7A7" : "\uE783", n == 0, 5);
                    };
                }
                break;
            case JobState.Failed:
                Set("Couldn't finish", string.Join("  ·  ", j.Errors), "\uE783", true, 12);
                buttonText = "Copy";
                onButton = onClick = () => { try { Clipboard.SetText(string.Join(Environment.NewLine, j.Errors)); } catch { } };
                break;
            default:
                if (j.ResultText != null)
                {
                    var text = j.ResultText;
                    try { Clipboard.SetText(text); } catch { }
                    Set(j.Act.Id == "qr" ? "QR code copied" : "Text copied", OneLine(text), "\uE8C8", false, 8);
                    if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    {
                        buttonText = "Open";
                        onButton = onClick = () => Open(uri.AbsoluteUri);
                    }
                }
                else if (j.Outputs.Count == 0)
                {
                    Set("Done", j.Note ?? "Nothing needed to change.", "\uE946", false, 6);
                }
                else
                {
                    var names = j.Outputs.Count == 1 ? Path.GetFileName(j.Outputs[0]) : $"{j.Outputs.Count} files saved";
                    var detail = j.Note != null ? $"{names}  ·  {j.Note}" : names;
                    if (j.Errors.Count > 0) detail += $"  ·  {j.Errors.Count} failed";
                    Set(j.Outputs.Count == 1 ? "Saved" : "All done", detail, "\uE73E", false, 6);
                    var first = j.Outputs[0];
                    buttonText = "Show";
                    onButton = onClick = () => Reveal(first);
                }
                break;
        }
        Present();
    }

    public void Message(string heading, string text, string icon = "", bool isError = false, int seconds = 8,
        string? button = null, Action? action = null)
    {
        if (running) return; // never hide live progress behind a message
        job = null;
        Set(heading, text, icon, isError, seconds);
        buttonText = button;
        onButton = onClick = action;
        Present();
    }

    /// <summary>Renders a banner off-screen without showing a window (README screenshots).</summary>
    internal Bitmap RenderPreview(string heading, string text, string glyph, string? button, double progressValue, float scale)
    {
        Fonts(scale);
        title = heading;
        body = text;
        badge = glyph;
        buttonText = button;
        running = progressValue >= 0;
        progress = progressValue;
        error = false;
        hovered = false;
        LayoutCard();
        int m = S(Shadow);
        var bmp = new Bitmap(card.Width + 2 * m, card.Height + 2 * m, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        PaintCard(g);
        running = false;
        return bmp;
    }

    // ---------- state ----------

    void Set(string t, string b, string glyph, bool isError, int seconds)
    {
        title = t;
        body = b;
        badge = glyph;
        error = isError;
        hideAt = DateTime.UtcNow.AddSeconds(seconds);
    }

    static string OneLine(string text)
    {
        var line = text.Replace("\r", "").Replace('\n', ' ').Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); } catch { }
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Present()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        float scale = Native.DpiScaleAt(new Point(area.Right - 10, area.Top + 10));
        if (Math.Abs(scale - s) > 0.01f || titleFont == null) Fonts(scale);
        LayoutCard();
        rest = new Point(area.Right - card.Right - S(10), area.Top + S(10) - card.Top);

        bool wasHidden = !Visible || closing;
        closing = false;
        fade = 1;
        if (wasHidden)
        {
            slide = 0;
            animStart = DateTime.UtcNow;
        }
        Render();
        if (!Visible) Show();
        tick.Start();
    }

    void Dismiss(bool animate = true)
    {
        if (!Visible) return;
        if (!animate) { tick.Stop(); Hide(); return; }
        closing = true;
        animStart = DateTime.UtcNow;
    }

    void OnTick()
    {
        var now = DateTime.UtcNow;
        double t = (now - animStart).TotalMilliseconds;
        bool animating = false;

        if (closing)
        {
            fade = (float)Math.Max(0, 1 - t / FadeOutMs);
            if (fade <= 0) { closing = false; tick.Stop(); Hide(); return; }
            animating = true;
        }
        else if (slide < 1)
        {
            slide = (float)Math.Min(1, t / SlideInMs);
            animating = true;
        }

        if (running && job != null)
        {
            progress = job.Progress;
            var name = Path.GetFileName(job.Current.TrimEnd('\\'));
            var count = job.Act.Batch == null && job.Inputs.Count > 1 ? $"{job.Index + 1} of {job.Inputs.Count}  ·  " : "";
            body = count + name;
            pulse++;
        }
        else if (!closing && now >= hideAt && !hovered)
        {
            Dismiss();
            return;
        }
        if (hovered && !running) hideAt = now.AddSeconds(Math.Max(3, (hideAt - now).TotalSeconds));

        // Animations render every frame; progress needs ~20 fps; idle needs nothing.
        if (animating || (running && (now - lastRender).TotalMilliseconds > 50)) Render();
    }

    // ---------- layout & painting ----------

    int S(float v) => (int)Math.Round(v * s);

    void Fonts(float scale)
    {
        s = scale;
        foreach (var f in new[] { titleFont, bodyFont, timeFont, buttonFont, glyphFont }) f?.Dispose();
        titleFont = new Font(Theme.UiFontSemibold, 13.5f * s, GraphicsUnit.Pixel);
        bodyFont = new Font(Theme.UiFont, 12.5f * s, GraphicsUnit.Pixel);
        timeFont = new Font(Theme.UiFont, 11f * s, GraphicsUnit.Pixel);
        buttonFont = new Font(Theme.UiFontSemibold, 12f * s, GraphicsUnit.Pixel);
        glyphFont = new Font(Theme.IconFont, 9f * s, GraphicsUnit.Pixel);
        icon?.Dispose();
        icon = AppIcon.Bitmap(S(40));
    }

    void LayoutCard()
    {
        int w = S(BaseWidth), m = S(Shadow);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        int textW = w - S(60) - S(16);
        float bodyH = Math.Min(g.MeasureString(body, bodyFont!, textW).Height, bodyFont!.Height * 2.05f);
        int h = S(12) + S(19) + (int)Math.Ceiling(bodyH) + (running ? S(14) : 0) + S(13);
        h = Math.Max(h, S(66));
        card = new Rectangle(m, m, w, h);
        closeRect = new Rectangle(card.X - S(7), card.Y - S(7), S(20), S(20));
        buttonRect = Rectangle.Empty;
        if (buttonText != null)
        {
            var size = g.MeasureString(buttonText, buttonFont!);
            int bw = (int)size.Width + S(22), bh = S(26);
            buttonRect = new Rectangle(card.Right - bw - S(12), card.Y + (card.Height - bh) / 2, bw, bh);
        }
    }

    void Render()
    {
        if (!IsHandleCreated) CreateHandle();
        lastRender = DateTime.UtcNow;
        int m = S(Shadow);
        using var bmp = new Bitmap(card.Width + 2 * m, card.Height + 2 * m, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            PaintCard(g);
        }
        float ease = 1 - MathF.Pow(1 - slide, 3);
        var at = new Point(rest.X + (int)((1 - ease) * S(70)), rest.Y);
        float alpha = (closing ? fade : Math.Min(1, slide * 1.6f));
        Native.PushLayered(Handle, bmp, at, (byte)(255 * Math.Clamp(alpha, 0, 1)));
        UpdateBounds();
    }

    void PaintCard(Graphics g)
    {
        // Soft shadow: stacked translucent rounded rects, offset down a little.
        int m = S(Shadow);
        for (int i = m; i > 0; i -= Math.Max(1, S(2)))
        {
            var r = card;
            r.Inflate(i, i);
            r.Offset(0, S(4));
            using var path = Draw.Round(r, S(Radius) + i);
            using var b = new SolidBrush(Color.FromArgb(7, 0, 4, 16));
            g.FillPath(b, path);
        }

        using (var path = Draw.Round(card, S(Radius)))
        {
            using (var b = new SolidBrush(Color.FromArgb(244, theme.Back))) g.FillPath(b, path);
            using (var sheen = new LinearGradientBrush(card, Color.FromArgb(22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillPath(sheen, path);
            using var edge = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, s));
            g.DrawPath(edge, path);
        }

        // App icon with a status badge, like macOS.
        var iconRect = new Rectangle(card.X + S(12), card.Y + S(12), S(40), S(40));
        if (icon != null) g.DrawImage(icon, iconRect);
        if (badge.Length > 0)
        {
            var dot = new RectangleF(iconRect.Right - S(14), iconRect.Bottom - S(14), S(18), S(18));
            using (var ring = new SolidBrush(theme.Back)) g.FillEllipse(ring, RectangleF.Inflate(dot, S(2), S(2)));
            using (var fill = new SolidBrush(error ? theme.Danger : theme.Accent)) g.FillEllipse(fill, dot);
            DrawText(g, badge, glyphFont!, theme.AccentText, dot, StringAlignment.Center);
        }

        // Prompts keep their button visible; "Cancel" on a running job appears on hover.
        bool showButton = buttonText != null && (!running || hovered);
        int x = card.X + S(60);
        int right = (showButton ? buttonRect.Left - S(8) : card.Right - S(14));
        DrawText(g, title, titleFont!, theme.Text, new RectangleF(x, card.Y + S(11), right - x - S(34), S(20)), StringAlignment.Near);
        if (!showButton)
            DrawText(g, "now", timeFont!, theme.SubText, new RectangleF(card.Right - S(60), card.Y + S(12), S(46), S(18)), StringAlignment.Far);

        var bodyRect = new RectangleF(x, card.Y + S(31), right - x, bodyFont!.Height * 2.1f);
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit })
        using (var b = new SolidBrush(theme.SubText))
            g.DrawString(body, bodyFont, b, bodyRect, sf);

        if (running)
        {
            var track = new RectangleF(x, card.Bottom - S(16), card.Right - S(16) - x, S(4));
            Draw.Fill(g, track, S(2), theme.Surface);
            if (progress >= 0)
                Draw.Fill(g, new RectangleF(track.X, track.Y, Math.Max(S(4), (float)(track.Width * progress)), track.Height), S(2), theme.Accent);
            else
            {
                float seg = track.Width * 0.3f, t = (pulse % 50) / 50f;
                var state = g.Save();
                g.SetClip(track);
                Draw.Fill(g, new RectangleF(track.X - seg + (track.Width + seg) * t, track.Y, seg, track.Height), S(2), theme.Accent);
                g.Restore(state);
            }
        }

        if (showButton)
        {
            Draw.Fill(g, buttonRect, buttonRect.Height / 2f, hoverButton ? theme.Accent : theme.Surface);
            DrawText(g, buttonText!, buttonFont!, hoverButton ? theme.AccentText : theme.Text, buttonRect, StringAlignment.Center);
        }

        if (hovered)
        {
            using (var b = new SolidBrush(hoverClose ? Color.FromArgb(255, 70, 90, 130) : Color.FromArgb(255, 48, 64, 98))) g.FillEllipse(b, closeRect);
            using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, s))) g.DrawEllipse(p, closeRect);
            DrawText(g, "\uE711", glyphFont!, theme.Text, closeRect, StringAlignment.Center);
        }
    }

    static void DrawText(Graphics g, string text, Font font, Color color, RectangleF r, StringAlignment align)
    {
        using var sf = new StringFormat
        {
            Alignment = align, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
        };
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, r, sf);
    }

    // ---------- mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool inCard = card.Contains(e.Location) || closeRect.Contains(e.Location);
        bool c = inCard && closeRect.Contains(e.Location);
        bool b = inCard && buttonText != null && buttonRect.Contains(e.Location);
        if (inCard != hovered || c != hoverClose || b != hoverButton)
        {
            hovered = inCard; hoverClose = c; hoverButton = b;
            Cursor = b || (inCard && !c && onClick != null) ? Cursors.Hand : Cursors.Default;
            Render();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = hoverClose = hoverButton = false;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !hovered) return;
        if (hoverClose) { Dismiss(); return; } // while running this only hides the banner
        var action = hoverButton ? onButton : running ? null : onClick;
        if (action == null) return;
        if (!running) Dismiss();
        action();
    }
}
