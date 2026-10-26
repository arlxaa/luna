using System.Diagnostics;
using Luna.Engines;

namespace Luna.DevTools;

/// <summary>
/// End-to-end check: generates sample files (FFmpeg, Edge, Office when present) and runs every
/// format and tool the ring would offer for them, printing one line per action.
/// Exit code 0 when nothing failed unexpectedly.
/// </summary>
internal static class SmokeCommand
{
    static int ok, failed, skipped, expected;

    // Tools that legitimately find nothing on synthetic images.
    static readonly string[] ExpectedMessages = { "No QR code found", "No text was recognized" };

    public static int Run(string dir)
    {
        if (Tools.Ffmpeg is null)
        {
            Console.WriteLine("FFmpeg is required for the samples: winget install Gyan.FFmpeg");
            return 1;
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, name);

        Console.WriteLine($"Creating samples in {dir}");
        var sets = CreateSamples(dir).Select(f => new[] { f }).ToList();
        sets.Add(new[] { P("alpha.png"), P("photo.jpg") });

        foreach (var set in sets)
        {
            var model = Catalog.Build(set);
            Console.WriteLine($"\n=== {string.Join(", ", set.Select(Path.GetFileName))}");
            foreach (var act in model.Formats.Concat(model.Tools)) RunAct(act, model.Files);
        }

        // Archives created above get their own round: extract and convert.
        foreach (var archive in new[] { "folder.zip", "folder.7z", "folder.tar.gz" }.Select(P).Where(File.Exists))
        {
            var model = Catalog.Build(new[] { archive });
            Console.WriteLine($"\n=== {Path.GetFileName(archive)}");
            foreach (var act in model.Formats.Concat(model.Tools)) RunAct(act, model.Files);
        }

        Console.WriteLine($"\n{ok} passed, {failed} failed, {skipped} skipped (engine missing), {expected} found nothing as expected");
        return failed == 0 ? 0 : 1;
    }

    static void RunAct(Act act, IReadOnlyList<string> files)
    {
        var missing = Tools.Missing(act.Needs);
        if (missing != Need.None)
        {
            skipped++;
            Console.WriteLine($"  SKIP {act.Label,-22} needs {Tools.Describe(missing)}");
            return;
        }
        if (act.Destructive)
        {
            CheckDelete(files);
            return;
        }
        var job = new Job { Act = act, Inputs = files };
        var sw = Stopwatch.StartNew();
        try
        {
            job.Current = files[0];
            if (act.Batch != null) act.Batch(job, files);
            else foreach (var f in files) { job.Current = f; act.PerFile!(job, f); }
            ok++;
            var result = job.ResultText != null ? $"text \"{job.ResultText.ReplaceLineEndings(" / ")}\""
                : job.Outputs.Count > 0 ? string.Join(", ", job.Outputs.Select(Path.GetFileName))
                : job.Note ?? "";
            Console.WriteLine($"  OK   {act.Label,-22} {sw.ElapsedMilliseconds,6} ms  {result}");
        }
        catch (Exception ex) when (ExpectedMessages.Any(m => ex.Message.StartsWith(m)))
        {
            expected++;
            Console.WriteLine($"  --   {act.Label,-22} {sw.ElapsedMilliseconds,6} ms  {ex.Message}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  FAIL {act.Label,-22} {sw.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message}");
        }
        finally { job.EndOffice(); }
    }

    /// <summary>
    /// Deletes copies of the files (the originals are still needed) to the Recycle Bin, then
    /// checks that Undo puts every one back.
    /// </summary>
    static void CheckDelete(IReadOnlyList<string> files)
    {
        var dir = Path.Combine(Path.GetDirectoryName(files[0])!, "delete-test");
        Directory.CreateDirectory(dir);
        var copies = files.Select(f => CopyTo(f, dir)).ToList();
        var act = Catalog.Build(copies).Tools.First(t => t.Destructive);
        var job = new Job { Act = act, Inputs = copies, Current = copies[0] };
        var sw = Stopwatch.StartNew();
        try
        {
            act.Batch!(job, copies);
            bool gone = copies.All(c => !File.Exists(c) && !Directory.Exists(c));
            int restored = job.Undo?.Invoke() ?? 0;
            bool back = copies.All(c => File.Exists(c) || Directory.Exists(c));
            if (gone && back && restored == copies.Count)
            {
                ok++;
                Console.WriteLine($"  OK   {act.Label,-22} {sw.ElapsedMilliseconds,6} ms  recycled and restored {restored}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL {act.Label,-22} {sw.ElapsedMilliseconds,6} ms  gone={gone} restored={restored}/{copies.Count} back={back}");
            }
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  FAIL {act.Label,-22} {sw.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message}");
        }
        finally { Directory.Delete(dir, true); }
    }

    static string CopyTo(string source, string dir)
    {
        var target = Path.Combine(dir, Path.GetFileName(source));
        if (File.Exists(source)) File.Copy(source, target);
        else
        {
            foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, d)));
            Directory.CreateDirectory(target);
            foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(target, Path.GetRelativePath(source, f)));
        }
        return target;
    }

    // ---------- samples ----------

    static List<string> CreateSamples(string dir)
    {
        string P(string name) => Path.Combine(dir, name);
        var files = new List<string>();

        void Ffmpeg(string output, params string[] args)
        {
            try
            {
                Proc.Run(Tools.Ffmpeg!, new[] { "-v", "error", "-y" }.Concat(args).Append(P(output)), CancellationToken.None);
                files.Add(P(output));
            }
            catch (Exception ex) { Console.WriteLine($"  (no {output}: {ex.Message})"); }
        }

        Ffmpeg("alpha.png", "-f", "lavfi", "-i",
            "color=c=blue:s=320x200,format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(between(X,60,259)*between(Y,40,159),255,0)'",
            "-frames:v", "1");
        Ffmpeg("photo.jpg", "-f", "lavfi", "-i", "testsrc2=s=640x480", "-frames:v", "1", "-q:v", "2");
        AddExifOrientation(P("photo.jpg"));
        Ffmpeg("anim.gif", "-f", "lavfi", "-i", "testsrc=s=200x150:r=10", "-t", "1.5");
        Ffmpeg("qr.png", "-f", "lavfi", "-i", "qrencodesrc=text='https\\://github.com/arlxaa/luna':q=256", "-frames:v", "1");
        Ffmpeg("clip.mp4", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30", "-f", "lavfi", "-i", "sine=f=440:sample_rate=48000",
            "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        Ffmpeg("tone.wav", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-f", "lavfi", "-i", "sine=f=330:sample_rate=44100",
            "-filter_complex", "[0]atrim=0:1[a];[1]atrim=0:2,volume=0.5[b];[0]atrim=0:1[c];[a][b][c]concat=n=3:v=0:a=1",
            "-metadata", "title=Secret Title", "-metadata", "artist=Someone");

        File.WriteAllText(P("page.html"), """
            <!doctype html><meta charset="utf-8"><body style="font:28px Segoe UI">
            <h1>Hello Luna</h1><p>Page one text for OCR testing.</p>
            <div style="page-break-after:always"></div><h1>Second page</h1><p>More text here.</p>
            """);
        File.WriteAllText(P("notes.md"), """
            # Luna notes

            Some **bold**, *italic*, `code` and a [link](https://github.com/arlxaa/luna).

            - one
            - two
              - nested **item**
            - [x] done task

            | Name | Value |
            |:-----|------:|
            | a    | 1     |

            ```python
            print("hello <world>")
            ```
            """);
        File.WriteAllText(P("plain.txt"), "Hello from a plain text file.\nLine two with <angle> & ampersand.\n");
        File.WriteAllText(P("data.csv"), "name,qty,price\napple,3,1.5\npear,5,2.25\n");
        Directory.CreateDirectory(P(@"folder\sub"));
        Directory.CreateDirectory(P(@"folder\empty"));
        File.WriteAllText(P(@"folder\a.txt"), "a");
        File.WriteAllText(P(@"folder\sub\b.txt"), "b");
        files.AddRange(new[] { P("notes.md"), P("plain.txt"), P("page.html"), P("data.csv"), P("folder") });

        // Derived samples, made with Luna's own conversions when their engines exist.
        Derive(P("page.html"), "PDF", P("page.pdf"), P("doc.pdf"), files);
        Derive(P("notes.md"), "DOCX", P("notes.docx"), null, files);
        Derive(P("data.csv"), "XLSX", P("data.xlsx"), null, files);
        if (Tools.PowerPoint) files.Add(MakeDeck(P("deck.pptx")));
        return files;
    }

    static void Derive(string source, string format, string output, string? renameTo, List<string> files)
    {
        var act = Catalog.Build(new[] { source }).Formats.FirstOrDefault(f => f.Label == format);
        if (act == null || !Tools.Has(act.Needs)) return;
        var job = new Job { Act = act, Inputs = new[] { source } };
        try { act.PerFile!(job, source); }
        catch (Exception ex) { Console.WriteLine($"  (no {Path.GetFileName(output)}: {ex.Message})"); return; }
        finally { job.EndOffice(); }
        if (renameTo != null) File.Move(output, renameTo);
        files.Add(renameTo ?? output);
    }

    static string MakeDeck(string path)
    {
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        dynamic deck = app.Presentations.Add(0);
        for (int i = 1; i <= 2; i++)
        {
            dynamic slide = deck.Slides.Add(i, 1); // ppLayoutTitle
            slide.Shapes.Item(1).TextFrame.TextRange.Text = $"Slide {i}";
            slide.Shapes.Item(2).TextFrame.TextRange.Text = "Made by the Luna smoke test";
        }
        deck.BuiltInDocumentProperties.Item("Author").Value = "Secret Author";
        deck.SaveAs(path, 24); // ppSaveAsOpenXMLPresentation
        deck.Close();
        if (app.Presentations.Count == 0) app.Quit();
        return path;
    }

    /// <summary>Adds an EXIF block (camera make + orientation 6) so metadata and rotation handling get exercised.</summary>
    static void AddExifOrientation(string jpeg)
    {
        if (!File.Exists(jpeg)) return;
        var data = File.ReadAllBytes(jpeg);
        var tiff = new List<byte> { (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8, 0, 2 };
        tiff.AddRange(new byte[] { 0x01, 0x0F, 0, 2, 0, 0, 0, 8, 0, 0, 0, 38 }); // Make, ASCII[8] at offset 38
        tiff.AddRange(new byte[] { 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0 });  // Orientation = 6
        tiff.AddRange(new byte[] { 0, 0, 0, 0 });
        tiff.AddRange("TestCam\0"u8.ToArray());
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        int len = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload);
        File.WriteAllBytes(jpeg, data.Take(2).Concat(segment).Concat(data.Skip(2)).ToArray());
    }
}
