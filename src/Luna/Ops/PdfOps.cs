using Luna.Engines;

namespace Luna.Ops;

internal static class PdfOps
{
    static string Qpdf => Tools.Qpdf ?? throw new ToolException("qpdf is not installed.");

    public static void ToImages(Job job, string pdf, string fmt)
    {
        // Peek at the page count by rendering into a temp folder first, then move into place.
        var temp = Paths.TempDir();
        try
        {
            var pages = WinRt.RenderPdf(pdf, Path.Combine(temp, Paths.BaseName(pdf)), fmt, 2.0, job.Token, job.Report);
            if (pages.Count == 1)
            {
                var output = Paths.Out(pdf, fmt);
                File.Move(pages[0], output);
                job.Outputs.Add(output);
            }
            else
            {
                var dir = Paths.OutDir(pdf, "pages");
                Directory.CreateDirectory(dir);
                foreach (var page in pages) File.Move(page, Path.Combine(dir, Path.GetFileName(page)));
                job.Outputs.Add(dir);
            }
        }
        finally { Paths.TryDelete(temp); }
    }

    public static void Merge(Job job, IReadOnlyList<string> pdfs)
    {
        var sorted = pdfs.OrderBy(p => p, Comparer<string>.Create(Paths.Logical)).ToList();
        var output = Paths.Unique(Path.Combine(Path.GetDirectoryName(sorted[0])!, "Merged.pdf"));
        var args = new List<string> { "--empty", "--pages" };
        args.AddRange(sorted);
        args.Add("--");
        args.Add(output);
        RunQpdf(job, args, output);
    }

    public static void Split(Job job, string pdf)
    {
        var dir = Paths.OutDir(pdf, "pages");
        Directory.CreateDirectory(dir);
        try { RunQpdf(job, new[] { "--split-pages", pdf, Path.Combine(dir, Paths.BaseName(pdf) + "-%d.pdf") }, null); }
        catch { Paths.TryDelete(dir); throw; }
        job.Outputs.Add(dir);
    }

    public static void Rotate(Job job, string pdf)
    {
        var output = Paths.Out(pdf, "pdf", "rotated");
        RunQpdf(job, new[] { pdf, "--rotate=+90", output }, output);
    }

    public static void Unlock(Job job, string pdf)
    {
        var output = Paths.Out(pdf, "pdf", "unlocked");
        try { RunQpdf(job, new[] { "--decrypt", pdf, output }, output); }
        catch (ToolException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("This PDF needs a password to open, so it can't be unlocked.");
        }
    }

    public static void Compress(Job job, string pdf)
    {
        var output = Paths.Out(pdf, "pdf", "compressed");
        if (Tools.Ghostscript is { } gs)
        {
            // Ghostscript re-renders images at 150 dpi: the big win for scanned PDFs.
            Proc.Run(gs, new[]
            {
                "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6",
                "-dPDFSETTINGS=/ebook", "-dDetectDuplicateImages=true", $"-sOutputFile={output}", pdf,
            }, job.Token);
        }
        else
        {
            RunQpdf(job, new[]
            {
                pdf, "--object-streams=generate", "--compress-streams=y", "--recompress-flate",
                "--compression-level=9", "--optimize-images", "--jpeg-quality=70", output,
            }, output, record: false);
        }
        long before = new FileInfo(pdf).Length, after = File.Exists(output) ? new FileInfo(output).Length : long.MaxValue;
        if (after >= before)
        {
            Paths.TryDelete(output);
            job.Note = "This PDF is already as small as it gets" + (Tools.Ghostscript is null ? " (install Ghostscript for stronger compression)." : ".");
            return;
        }
        job.Outputs.Add(output);
        job.Note = $"{ImageOps.Size(before)} → {ImageOps.Size(after)} ({100 - after * 100 / before}% smaller)";
    }

    static void RunQpdf(Job job, IEnumerable<string> args, string? output, bool record = true)
    {
        try
        {
            // Exit code 3 means "succeeded with warnings", which is fine for our purposes.
            Proc.Run(Qpdf, args.Prepend("--warning-exit-0"), job.Token);
        }
        catch
        {
            if (output != null) Paths.TryDelete(output);
            throw;
        }
        if (output != null && record) job.Outputs.Add(output);
    }
}
