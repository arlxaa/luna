using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Luna.Engines;

namespace Luna.Ops;

internal static class DocOps
{
    // Word SaveAs2 formats (WdSaveFormat).
    static int WordFormat(string fmt) => fmt switch
    {
        "docx" => 16, "doc" => 0, "odt" => 23, "rtf" => 6, "txt" => 2, "html" => 10,
        _ => throw new ToolException($"Word can't save .{fmt}."),
    };

    // ---------- Word ----------

    public static void WordConvert(Job job, string input, string fmt)
    {
        var output = Paths.Out(input, fmt);
        WordSave(job, input, output, fmt);
        job.Outputs.Add(output);
    }

    static void WordSave(Job job, string input, string output, string fmt)
    {
        if (Paths.Ext(input) == "pdf") OfficeSession.AllowSilentPdfOpen();
        dynamic word = job.Office.Word;
        dynamic doc = OpenWord(word, input);
        try
        {
            if (fmt == "pdf")
                doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17, OpenAfterExport: false,
                    OptimizeFor: 0, CreateBookmarks: 1, DocStructureTags: true);
            else if (fmt == "txt")
                doc.SaveAs2(FileName: output, FileFormat: 2, AddToRecentFiles: false, Encoding: 65001);
            else
                doc.SaveAs2(FileName: output, FileFormat: WordFormat(fmt), AddToRecentFiles: false);
        }
        finally
        {
            try { doc.Close(SaveChanges: 0); } catch { }
            OfficeSession.Release(doc);
        }
    }

    static dynamic OpenWord(dynamic word, string input)
    {
        try
        {
            // A dummy password makes protected files fail fast instead of showing a hidden prompt.
            return word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true,
                AddToRecentFiles: false, PasswordDocument: "\u0001luna", Visible: false, NoEncodingDialog: true);
        }
        catch (COMException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("This document is password-protected.");
        }
    }

    public static void WordStripMetadata(Job job, string input)
    {
        var output = Paths.Out(input, Paths.Ext(input), "clean");
        dynamic doc = OpenWord(job.Office.Word, input);
        try
        {
            doc.RemoveDocumentInformation(8); // wdRDIDocumentProperties
            doc.RemoveDocumentInformation(4); // wdRDIRemovePersonalInformation
            doc.SaveAs2(FileName: output, FileFormat: doc.SaveFormat, AddToRecentFiles: false);
        }
        finally
        {
            try { doc.Close(SaveChanges: 0); } catch { }
            OfficeSession.Release(doc);
        }
        job.Outputs.Add(output);
    }

    // ---------- Excel ----------

    public static void ExcelConvert(Job job, string input, string fmt)
    {
        var output = Paths.Out(input, fmt);
        dynamic wb = OpenExcel(job, input);
        try
        {
            if (fmt == "pdf")
                wb.ExportAsFixedFormat(Type: 0, Filename: output, Quality: 0, IncludeDocProperties: true,
                    IgnorePrintAreas: false, OpenAfterPublish: false);
            else
                wb.SaveAs(Filename: output, AddToMru: false, FileFormat: fmt switch
                {
                    "xlsx" => 51, "xls" => 56, "csv" => 62, "ods" => 60, "html" => 44,
                    _ => throw new ToolException($"Excel can't save .{fmt}."),
                });
        }
        finally
        {
            try { wb.Close(SaveChanges: false); } catch { }
            OfficeSession.Release(wb);
        }
        job.Outputs.Add(output);
    }

    static dynamic OpenExcel(Job job, string input)
    {
        try
        {
            return job.Office.Excel.Workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true,
                Password: "\u0001luna", IgnoreReadOnlyRecommended: true, AddToMru: false, Notify: false);
        }
        catch (COMException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("This workbook is password-protected.");
        }
    }

    public static void ExcelStripMetadata(Job job, string input)
    {
        var output = Paths.Out(input, Paths.Ext(input), "clean");
        dynamic wb = OpenExcel(job, input);
        try
        {
            wb.RemoveDocumentInformation(8); // xlRDIDocumentProperties
            wb.RemoveDocumentInformation(4); // xlRDIRemovePersonalInformation
            wb.SaveAs(Filename: output, FileFormat: wb.FileFormat, AddToMru: false);
        }
        finally
        {
            try { wb.Close(SaveChanges: false); } catch { }
            OfficeSession.Release(wb);
        }
        job.Outputs.Add(output);
    }

    // ---------- PowerPoint ----------

    static int PptFormat(string ext) => ext switch
    {
        "pdf" => 32, "pptx" => 24, "ppt" => 1, "odp" => 35, "pptm" => 25, "ppsx" => 28, "pps" => 7, "potx" => 26, "pot" => 5,
        _ => 11, // ppSaveAsDefault
    };

    static dynamic OpenPowerPoint(Job job, string input) =>
        job.Office.PowerPoint.Presentations.Open(FileName: input, ReadOnly: -1, Untitled: 0, WithWindow: 0);

    public static void PowerPointConvert(Job job, string input, string fmt)
    {
        dynamic pres = OpenPowerPoint(job, input);
        try
        {
            switch (fmt)
            {
                case "png":
                case "jpg":
                    ExportSlides(job, pres, input, fmt);
                    break;
                case "mp4":
                    ExportVideo(job, pres, input);
                    break;
                default:
                    var output = Paths.Out(input, fmt);
                    pres.SaveAs(FileName: output, FileFormat: PptFormat(fmt));
                    job.Outputs.Add(output);
                    break;
            }
        }
        finally
        {
            try { pres.Close(); } catch { }
            OfficeSession.Release(pres);
        }
    }

    static void ExportSlides(Job job, dynamic pres, string input, string fmt)
    {
        int count = pres.Slides.Count;
        int width = (int)Math.Round((double)pres.PageSetup.SlideWidth * 2);
        int height = (int)Math.Round((double)pres.PageSetup.SlideHeight * 2);
        string dir = count == 1 ? Path.GetDirectoryName(input)! : Paths.OutDir(input, "slides");
        Directory.CreateDirectory(dir);
        var name = Paths.BaseName(input);
        for (int i = 1; i <= count; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            var path = count == 1 ? Paths.Out(input, fmt) : Path.Combine(dir, $"{name}-{i:D2}.{fmt}");
            dynamic slide = pres.Slides.Item(i);
            slide.Export(path, fmt == "png" ? "PNG" : "JPG", width, height);
            OfficeSession.Release(slide);
            if (count == 1) job.Outputs.Add(path);
            job.Report((double)i / count);
        }
        if (count > 1) job.Outputs.Add(dir);
    }

    static void ExportVideo(Job job, dynamic pres, string input)
    {
        var output = Paths.Out(input, "mp4");
        pres.CreateVideo(output, true, 5, 1080, 30, 85);
        while (true)
        {
            Thread.Sleep(500);
            job.Token.ThrowIfCancellationRequested();
            int status = pres.CreateVideoStatus; // 1 in progress, 2 queued, 3 done, 4 failed
            if (status == 3) break;
            if (status == 4) throw new ToolException("PowerPoint couldn't export the video.");
        }
        job.Outputs.Add(output);
    }

    public static void PowerPointStripMetadata(Job job, string input)
    {
        var output = Paths.Out(input, Paths.Ext(input), "clean");
        dynamic pres = OpenPowerPoint(job, input);
        try
        {
            pres.RemoveDocumentInformation(8); // ppRDIDocumentProperties
            pres.RemoveDocumentInformation(4); // ppRDIRemovePersonalInformation
            pres.SaveAs(FileName: output, FileFormat: PptFormat(Paths.Ext(input)));
        }
        finally
        {
            try { pres.Close(); } catch { }
            OfficeSession.Release(pres);
        }
        job.Outputs.Add(output);
    }

    // ---------- HTML / Markdown / text (Edge + Word) ----------

    public static void EdgePdf(Job job, string html, string output)
    {
        var edge = Tools.Edge ?? throw new ToolException("Microsoft Edge was not found.");
        var profile = Paths.TempDir();
        try
        {
            Proc.Run(edge, new[]
            {
                "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                $"--user-data-dir={profile}", "--no-pdf-header-footer", "--run-all-compositor-stages-before-draw",
                $"--print-to-pdf={output}", new Uri(html).AbsoluteUri,
            }, job.Token, timeoutSeconds: 120);
        }
        finally { Paths.TryDelete(profile); }
        if (!File.Exists(output)) throw new ToolException("Edge could not print this page to PDF.");
        job.Outputs.Add(output);
    }

    public static void HtmlConvert(Job job, string input, string fmt)
    {
        if (fmt == "pdf") EdgePdf(job, input, Paths.Out(input, "pdf"));
        else WordConvert(job, input, fmt);
    }

    public static void TextConvert(Job job, string input, string fmt)
    {
        if (fmt != "pdf") { WordConvert(job, input, fmt); return; }
        var text = File.ReadAllText(input);
        var html = "<!doctype html><meta charset=utf-8><style>@page{margin:16mm}body{margin:0}" +
                   "pre{white-space:pre-wrap;word-wrap:break-word;font:10.5pt/1.45 Consolas,'Cascadia Mono',monospace}</style>" +
                   $"<pre>{WebUtility.HtmlEncode(text)}</pre>";
        WithSidecarHtml(input, html, temp => EdgePdf(job, temp, Paths.Out(input, "pdf")));
    }

    public static void MarkdownConvert(Job job, string input, string fmt)
    {
        var html = Markdown.ToDocument(File.ReadAllText(input), Paths.BaseName(input));
        switch (fmt)
        {
            case "html":
                var output = Paths.Out(input, "html");
                File.WriteAllText(output, html, new UTF8Encoding(false));
                job.Outputs.Add(output);
                break;
            case "pdf":
                WithSidecarHtml(input, html, temp => EdgePdf(job, temp, Paths.Out(input, "pdf")));
                break;
            default:
                WithSidecarHtml(input, html, temp =>
                {
                    var outPath = Paths.Out(input, fmt);
                    WordSave(job, temp, outPath, fmt);
                    job.Outputs.Add(outPath);
                });
                break;
        }
    }

    /// <summary>
    /// Writes a hidden temporary HTML file next to the source so relative image links resolve,
    /// runs <paramref name="use"/>, then deletes it.
    /// </summary>
    static void WithSidecarHtml(string input, string html, Action<string> use)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, $"~luna-{Guid.NewGuid():N}.html");
        File.WriteAllText(temp, html, new UTF8Encoding(false));
        try
        {
            File.SetAttributes(temp, FileAttributes.Hidden);
            use(temp);
        }
        finally { Paths.TryDelete(temp); }
    }
}
