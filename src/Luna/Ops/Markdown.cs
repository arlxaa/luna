using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Luna.Ops;

/// <summary>
/// Compact Markdown → HTML renderer covering what real-world READMEs and notes use: headings,
/// emphasis, links, images, code, lists (nested, task), quotes, tables, rules and raw HTML.
/// </summary>
internal static partial class Markdown
{
    const string Css = """
        :root{color-scheme:light}
        body{font:15px/1.6 -apple-system,"Segoe UI",system-ui,sans-serif;color:#1f2328;max-width:860px;margin:32px auto;padding:0 24px}
        h1,h2{border-bottom:1px solid #d8dee4;padding-bottom:.3em}
        h1,h2,h3,h4,h5,h6{margin:1.4em 0 .6em;line-height:1.25}
        code{font:.88em Consolas,"Cascadia Mono",monospace;background:#eff1f3;padding:.15em .35em;border-radius:5px}
        pre{background:#f6f8fa;padding:14px 16px;border-radius:8px;overflow:auto;line-height:1.45}
        pre code{background:none;padding:0;font-size:.85em}
        blockquote{margin:0;padding:0 1em;color:#59636e;border-left:.25em solid #d1d9e0}
        table{border-collapse:collapse;margin:1em 0}th,td{border:1px solid #d1d9e0;padding:6px 13px}th{background:#f6f8fa}
        img{max-width:100%}hr{border:0;border-top:1px solid #d1d9e0;margin:1.6em 0}a{color:#0969da}
        li.task{list-style:none;margin-left:-1.4em}
        @page{margin:16mm}@media print{body{margin:0;max-width:none}}
        """;

    public static string ToDocument(string markdown, string title) =>
        $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
        $"<style>{Css}</style></head><body>\n{ToHtml(markdown)}\n</body></html>\n";

    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        var sb = new StringBuilder();
        Blocks(lines, sb);
        return sb.ToString();
    }

    static void Blocks(IReadOnlyList<string> lines, StringBuilder sb)
    {
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var fence = FenceRx().Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                var lang = fence.Groups[2].Value.Trim();
                var code = new List<string>();
                for (i++; i < lines.Count && !lines[i].TrimStart().StartsWith(marker); i++) code.Add(lines[i]);
                i++;
                var cls = lang.Length > 0 ? $" class=\"language-{WebUtility.HtmlEncode(lang)}\"" : "";
                sb.Append($"<pre><code{cls}>{WebUtility.HtmlEncode(string.Join("\n", code))}</code></pre>\n");
                continue;
            }

            var heading = HeadingRx().Match(line);
            if (heading.Success)
            {
                int level = heading.Groups[1].Length;
                sb.Append($"<h{level}>{Inline(heading.Groups[2].Value)}</h{level}>\n");
                i++;
                continue;
            }

            if (RuleRx().IsMatch(line)) { sb.Append("<hr>\n"); i++; continue; }

            if (line.TrimStart().StartsWith('>'))
            {
                var quoted = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    var q = lines[i].TrimStart()[1..];
                    quoted.Add(q.StartsWith(' ') ? q[1..] : q);
                }
                sb.Append("<blockquote>\n");
                Blocks(quoted, sb);
                sb.Append("</blockquote>\n");
                continue;
            }

            if (ListItemRx().IsMatch(line))
            {
                i = List(lines, i, sb);
                continue;
            }

            if (i + 1 < lines.Count && line.Contains('|') && TableRuleRx().IsMatch(lines[i + 1]))
            {
                i = Table(lines, i, sb);
                continue;
            }

            if (line.TrimStart().StartsWith('<') && line.TrimEnd().EndsWith('>'))
            {
                for (; i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]); i++) sb.Append(lines[i]).Append('\n');
                continue;
            }

            // Paragraph (or setext heading).
            var para = new List<string>();
            bool wasSetext = false;
            while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (para.Count > 0 && SetextRx().Match(lines[i]) is { Success: true } setext)
                {
                    int level = setext.Value.TrimStart()[0] == '=' ? 1 : 2;
                    sb.Append($"<h{level}>{Inline(string.Join(" ", para.Select(p => p.Trim())))}</h{level}>\n");
                    i++;
                    wasSetext = true;
                    break;
                }
                if (para.Count > 0 && StartsBlock(lines[i])) break;
                para.Add(lines[i++]);
            }
            if (wasSetext) continue;
            var text = string.Join("\n", para.Select(p => p.EndsWith("  ") || p.EndsWith('\\') ? p.TrimEnd(' ', '\\') + "<br>" : p.Trim()));
            sb.Append($"<p>{Inline(text)}</p>\n");
        }
    }

    static bool StartsBlock(string line) =>
        FenceRx().IsMatch(line) || HeadingRx().IsMatch(line) || RuleRx().IsMatch(line) ||
        line.TrimStart().StartsWith('>') || ListItemRx().IsMatch(line);

    static int Indent(string line) => line.Length - line.TrimStart().Length;

    static int List(IReadOnlyList<string> lines, int i, StringBuilder sb)
    {
        int baseIndent = Indent(lines[i]);
        bool ordered = char.IsDigit(lines[i].TrimStart()[0]);
        var startMatch = ListItemRx().Match(lines[i]);
        int start = ordered && int.TryParse(startMatch.Groups[2].Value.TrimEnd('.', ')'), out var s) ? s : 1;
        sb.Append(ordered ? (start != 1 ? $"<ol start=\"{start}\">\n" : "<ol>\n") : "<ul>\n");

        while (i < lines.Count)
        {
            var m = ListItemRx().Match(lines[i]);
            if (!m.Success || Indent(lines[i]) != baseIndent) break;
            int contentIndent = baseIndent + m.Groups[2].Length + 1;
            var body = new List<string> { m.Groups[3].Value };
            bool loose = false;
            for (i++; i < lines.Count; i++)
            {
                var l = lines[i];
                if (string.IsNullOrWhiteSpace(l))
                {
                    // A blank line continues the item only if indented content follows.
                    if (i + 1 < lines.Count && !string.IsNullOrWhiteSpace(lines[i + 1]) && Indent(lines[i + 1]) > baseIndent)
                    { body.Add(""); loose = true; continue; }
                    break;
                }
                if (Indent(l) <= baseIndent && (ListItemRx().IsMatch(l) || StartsBlock(l))) break;
                if (Indent(l) <= baseIndent && body.Count > 0 && string.IsNullOrEmpty(body[^1])) break;
                body.Add(Indent(l) >= contentIndent ? l[contentIndent..] : l.TrimStart());
            }

            var first = body[0];
            string cls = "";
            var task = TaskRx().Match(first);
            if (task.Success)
            {
                bool done = task.Groups[1].Value is "x" or "X";
                first = $"<input type=\"checkbox\" disabled{(done ? " checked" : "")}> " + first[task.Length..];
                cls = " class=\"task\"";
            }
            sb.Append($"<li{cls}>");
            if (body.Count == 1 && !loose) sb.Append(task.Success ? InlineKeepTags(first) : Inline(first));
            else
            {
                var inner = new StringBuilder();
                var rest = body.ToList();
                rest[0] = task.Success ? "" : rest[0];
                Blocks(rest, inner);
                var html = inner.ToString();
                if (task.Success) sb.Append(InlineKeepTags(first));
                // Tight lists render their first paragraph without <p>.
                if (!loose && html.StartsWith("<p>"))
                {
                    int end = html.IndexOf("</p>\n", StringComparison.Ordinal);
                    html = html[3..end] + "\n" + html[(end + 5)..];
                }
                sb.Append(html);
            }
            sb.Append("</li>\n");
        }
        sb.Append(ordered ? "</ol>\n" : "</ul>\n");
        return i;
    }

    static string InlineKeepTags(string s)
    {
        int tagEnd = s.IndexOf('>') + 1;
        return s[..tagEnd] + Inline(s[tagEnd..]);
    }

    static int Table(IReadOnlyList<string> lines, int i, StringBuilder sb)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var head = Cells(lines[i]);
        var align = Cells(lines[i + 1]).Select(c =>
            c.StartsWith(':') && c.EndsWith(':') ? "center" : c.EndsWith(':') ? "right" : c.StartsWith(':') ? "left" : null).ToArray();
        string Style(int col) => col < align.Length && align[col] != null ? $" style=\"text-align:{align[col]}\"" : "";

        sb.Append("<table>\n<thead><tr>");
        for (int c = 0; c < head.Length; c++) sb.Append($"<th{Style(c)}>{Inline(head[c])}</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        for (i += 2; i < lines.Count && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]); i++)
        {
            var cells = Cells(lines[i]);
            sb.Append("<tr>");
            for (int c = 0; c < head.Length; c++) sb.Append($"<td{Style(c)}>{(c < cells.Length ? Inline(cells[c]) : "")}</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table>\n");
        return i;
    }

    /// <summary>Inline formatting. Code spans and raw tags are protected with placeholders first.</summary>
    static string Inline(string text)
    {
        var saved = new List<string>();
        string Keep(string html) { saved.Add(html); return $"\u0001{saved.Count - 1}\u0002"; }

        text = CodeSpanRx().Replace(text, m => Keep($"<code>{WebUtility.HtmlEncode(m.Groups[2].Value.Trim())}</code>"));
        text = AutoLinkRx().Replace(text, m => Keep($"<a href=\"{WebUtility.HtmlEncode(m.Groups[1].Value)}\">{WebUtility.HtmlEncode(m.Groups[1].Value)}</a>"));
        text = RawTagRx().Replace(text, m => Keep(m.Value));
        text = WebUtility.HtmlEncode(text).Replace("&#39;", "'");

        text = ImageRx().Replace(text, m => Keep($"<img src=\"{Attr(m.Groups[2].Value)}\" alt=\"{Attr(m.Groups[1].Value)}\">"));
        text = LinkRx().Replace(text, m => $"<a href=\"{Attr(m.Groups[2].Value)}\">{m.Groups[1].Value}</a>");
        text = BoldRx().Replace(text, "<strong>$2</strong>");
        text = ItalicRx().Replace(text, "<em>$2</em>");
        text = StrikeRx().Replace(text, "<del>$1</del>");
        text = BareUrlRx().Replace(text, m => $"<a href=\"{m.Value}\">{m.Value}</a>");

        return PlaceholderRx().Replace(text, m => saved[int.Parse(m.Groups[1].Value)]);
    }

    static string Attr(string s) => WebUtility.HtmlDecode(s).Replace("\"", "&quot;").Split(' ')[0];

    [GeneratedRegex(@"^\s*(```+|~~~+)(.*)$")] private static partial Regex FenceRx();
    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")] private static partial Regex RuleRx();
    [GeneratedRegex(@"^\s{0,3}(=+|-+)\s*$")] private static partial Regex SetextRx();
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$")] private static partial Regex ListItemRx();
    [GeneratedRegex(@"^\[([ xX])\]\s+")] private static partial Regex TaskRx();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableRuleRx();
    [GeneratedRegex(@"(`+)(.+?)\1")] private static partial Regex CodeSpanRx();
    [GeneratedRegex(@"<(https?://[^\s>]+)>")] private static partial Regex AutoLinkRx();
    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>")] private static partial Regex RawTagRx();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+(?:\s+&quot;[^)]*&quot;)?)\)")] private static partial Regex ImageRx();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+(?:\s+&quot;[^)]*&quot;)?)\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")] private static partial Regex BoldRx();
    [GeneratedRegex(@"(?<![\w*])(\*|_)(?=\S)(.+?)(?<=\S)\1(?![\w*])")] private static partial Regex ItalicRx();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")] private static partial Regex StrikeRx();
    [GeneratedRegex(@"(?<![""'=>\w])https?://[^\s<]+[^\s<.,;:!?)\]'""]")] private static partial Regex BareUrlRx();
    [GeneratedRegex("\u0001(\\d+)\u0002")] private static partial Regex PlaceholderRx();
}
