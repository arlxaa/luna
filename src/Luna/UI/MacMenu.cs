using System.Drawing.Drawing2D;

namespace Luna.UI;

/// <summary>
/// Styles a ContextMenuStrip like a macOS menu: rounded window, rounded selection highlight,
/// checkmarks in a left column, a "›" for submenus and hairline separators. Night palette.
/// </summary>
internal static class MacMenu
{
    static readonly Theme T = Theme.Night;
    static readonly Color Selection = Color.FromArgb(52, 94, 178);

    /// <summary>Tag for a dimmed, non-clickable title row (like a macOS menu section header).</summary>
    public const string Header = "header";

    public static ToolStripMenuItem Title(string text, Font menuFont) =>
        new(text) { Enabled = false, Tag = Header, Font = new Font(menuFont, FontStyle.Bold) };

    public static void Apply(ToolStripDropDownMenu menu)
    {
        menu.Renderer = new Renderer();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Padding = new Padding(0, 5, 0, 5);
        menu.Font = new Font(Theme.UiFont, 9.75f);
        menu.BackColor = T.Back;
        menu.ForeColor = T.Text;
        menu.Opened += (_, _) => Native.RoundCorners(menu.Handle);
        menu.ItemAdded += (_, e) => Style(e.Item);
        foreach (ToolStripItem item in menu.Items) Style(item);
    }

    static void Style(ToolStripItem? item)
    {
        if (item is not ToolStripMenuItem mi) return;
        mi.Padding = new Padding(2, 3, 2, 3);
        if (mi.DropDown is ToolStripDropDownMenu sub && sub.Renderer is not Renderer) Apply(sub);
    }

    sealed class Renderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(T.Back);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(T.Border);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(5, 1, e.Item.Width - 10, e.Item.Height - 2);
            Draw.Fill(g, r, 5, Selection);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool header = e.Item.Tag as string == Header;
            e.TextColor = header ? T.SubText : !e.Item.Enabled ? T.Disabled : e.Item.Selected ? Color.White : T.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var color = e.Item.Selected ? Color.White : T.Text;
            using var f = new Font(Theme.IconFont, e.Item.Font.Size * 0.95f);
            TextRenderer.DrawText(e.Graphics, "", f, e.ImageRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var color = e.Item?.Selected == true ? Color.White : T.SubText;
            TextRenderer.DrawText(e.Graphics, "›", e.Item?.Font ?? Control.DefaultFont, e.ArrowRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var p = new Pen(T.Separator);
            e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
        }
    }
}
