using System.Globalization;
using System.IO.Compression;
using System.Text;
using Luna.Engines;

namespace Luna.Ops;

/// <summary>Minimal PDF writer: one image per page, page size = image size at 96 DPI.</summary>
internal static class PdfWriter
{
    sealed record PageImage(int Width, int Height, int Components, string Filter, byte[] Data);

    static readonly string[] LossySources = { "jpg", "heic", "avif", "webp", "jxl" };
    static readonly string[] PassThroughPix = { "yuvj420p", "yuvj422p", "yuvj444p", "yuvj440p", "yuvj411p", "gray" };

    public static void FromImages(Job job, IReadOnlyList<string> inputs, string output)
    {
        var objects = new List<long>();
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        try
        {
            Write(fs, "%PDF-1.4\n%âãÏÓ\n");
            int n = inputs.Count;
            int ObjId(int page, int part) => 3 + page * 3 + part; // page dict, content, image

            Begin(fs, objects, 1);
            Write(fs, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            for (int p = 0; p < n; p++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Current = inputs[p];
                var img = Load(job, inputs[p]);
                var w = Num(img.Width * 0.75);
                var h = Num(img.Height * 0.75);

                Begin(fs, objects, ObjId(p, 0));
                Write(fs, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                          $"/Resources << /XObject << /Im0 {ObjId(p, 2)} 0 R >> >> /Contents {ObjId(p, 1)} 0 R >>\nendobj\n");

                var content = Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q");
                Begin(fs, objects, ObjId(p, 1));
                Write(fs, $"<< /Length {content.Length} >>\nstream\n");
                fs.Write(content);
                Write(fs, "\nendstream\nendobj\n");

                Begin(fs, objects, ObjId(p, 2));
                var cs = img.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
                Write(fs, $"<< /Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} /ColorSpace {cs} " +
                          $"/BitsPerComponent 8 /Filter /{img.Filter} /Length {img.Data.Length} >>\nstream\n");
                fs.Write(img.Data);
                Write(fs, "\nendstream\nendobj\n");
                job.Report((p + 1.0) / n);
            }

            Begin(fs, objects, 2);
            var kids = string.Join(" ", Enumerable.Range(0, n).Select(p => $"{ObjId(p, 0)} 0 R"));
            Write(fs, $"<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

            long xref = fs.Position;
            int size = objects.Count + 1;
            var sb = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
            for (int id = 1; id < size; id++) sb.Append($"{objects[id - 1]:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(fs, sb.ToString());
        }
        catch
        {
            fs.Dispose();
            Paths.TryDelete(output);
            throw;
        }
        job.Outputs.Add(output);
    }

    static void Begin(Stream s, List<long> offsets, int id)
    {
        while (offsets.Count < id) offsets.Add(0);
        offsets[id - 1] = s.Position;
        Write(s, $"{id} 0 obj\n");
    }

    static void Write(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));

    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static PageImage Load(Job job, string input)
    {
        var ext = ImageOps.Norm(Paths.Ext(input));
        var info = Ffmpeg.Probe(input, job.Token);
        if (!info.HasVideo) throw new ToolException($"{Path.GetFileName(input)} is not a readable image.");

        // Untouched JPEG bytes when the viewer will display them correctly as-is.
        if (ext == "jpg" && PassThroughPix.Contains(info.PixFmt) && MetadataOps.JpegOrientation(input) == 1)
            return new PageImage(info.Width, info.Height, info.PixFmt == "gray" ? 1 : 3, "DCTDecode", File.ReadAllBytes(input));

        var flatten = info.HasAlpha
            ? "format=rgba,split[fa][fb];[fa]drawbox=c=white@1:t=fill:replace=1[fbg];[fbg][fb]overlay=format=auto,"
            : "";
        if (LossySources.Contains(ext))
        {
            var jpeg = Ffmpeg.Pipe(job, new[] { "-i", input, "-frames:v", "1", "-vf", flatten + "format=yuvj444p", "-c:v", "mjpeg", "-q:v", "2", "-f", "image2pipe" });
            var (w, h, comps) = JpegSize(jpeg);
            return new PageImage(w, h, comps, "DCTDecode", jpeg);
        }

        var ppm = Ffmpeg.Pipe(job, new[] { "-i", input, "-frames:v", "1", "-vf", flatten + "format=rgb24", "-c:v", "ppm", "-f", "image2pipe" });
        var (pw, ph, offset) = PpmHeader(ppm);
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(ppm, offset, pw * ph * 3);
        return new PageImage(pw, ph, 3, "FlateDecode", packed.ToArray());
    }

    static (int w, int h, int comps) JpegSize(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte m = d[i + 1];
            if (m is 0xC0 or 0xC1 or 0xC2)
                return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6], d[i + 9]);
            if (m is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { i += 2; continue; }
            i += 2 + ((d[i + 2] << 8) | d[i + 3]);
        }
        throw new ToolException("Could not read the encoded JPEG.");
    }

    static (int w, int h, int offset) PpmHeader(byte[] d)
    {
        // "P6\n<w> <h>\n255\n" with arbitrary whitespace.
        int pos = 2;
        int Next()
        {
            while (pos < d.Length && char.IsWhiteSpace((char)d[pos])) pos++;
            int start = pos;
            while (pos < d.Length && !char.IsWhiteSpace((char)d[pos])) pos++;
            return int.Parse(Encoding.ASCII.GetString(d, start, pos - start), CultureInfo.InvariantCulture);
        }
        if (d.Length < 3 || d[0] != 'P' || d[1] != '6') throw new ToolException("Unexpected image data from FFmpeg.");
        int w = Next(), h = Next();
        Next(); // maxval
        return (w, h, pos + 1);
    }
}
