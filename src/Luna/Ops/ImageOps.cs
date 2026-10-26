using Luna.Engines;

namespace Luna.Ops;

internal static class ImageOps
{
    /// <summary>Formats FFmpeg can write as a still image.</summary>
    public static readonly string[] Writable = { "png", "jpg", "webp", "avif", "jxl", "gif", "bmp", "tiff" };

    // Paints transparent areas white (JPEG has no alpha channel).
    const string Flatten = "format=rgba,split[fa][fb];[fa]drawbox=c=white@1:t=fill:replace=1[fbg];[fbg][fb]overlay=format=auto";

    public static string Norm(string ext) => ext switch
    {
        "jpeg" or "jfif" or "jpe" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        _ => ext,
    };

    public static void Convert(Job job, string input, string fmt)
    {
        switch (fmt)
        {
            case "pdf": PdfWriter.FromImages(job, new[] { input }, Paths.Out(input, "pdf")); break;
            case "ico": MakeIcon(job, input, Paths.Out(input, "ico")); break;
            default: Encode(job, input, Paths.Out(input, fmt), fmt, null, compress: false); break;
        }
    }

    /// <summary>Applies an FFmpeg filter, writing the same format as the input when possible.</summary>
    public static void Transform(Job job, string input, string filter, string tag)
    {
        var fmt = KeepFormat(input);
        Encode(job, input, Paths.Out(input, fmt, tag), fmt, filter, compress: false);
    }

    public static void Compress(Job job, string input)
    {
        var fmt = KeepFormat(input);
        if (fmt is "bmp" or "tiff") fmt = "jpg";
        var output = Paths.Out(input, fmt, "compressed");
        Encode(job, input, output, fmt, null, compress: true);
        long before = new FileInfo(input).Length, after = new FileInfo(output).Length;
        if (after >= before * 0.97)
        {
            // Not worth a second copy.
            job.Outputs.Remove(output);
            Paths.TryDelete(output);
            job.Note = $"{Path.GetFileName(input)} is already as small as it gets.";
            return;
        }
        job.Note = $"{Size(before)} → {Size(after)} ({100 - after * 100 / Math.Max(1, before)}% smaller)";
    }

    public static void ReadQr(Job job, string input)
    {
        var codes = Ffmpeg.ReadQr(job, input);
        if (codes.Count == 0) throw new ToolException("No QR code found in this image.");
        job.ResultText = string.Join(Environment.NewLine, codes);
    }

    public static void Ocr(Job job, string input)
    {
        string source = input;
        string? temp = null;
        try
        {
            // Windows OCR reads common formats up to ~10k px; normalize everything else to a PNG first.
            var ext = Norm(Paths.Ext(input));
            var info = Ffmpeg.Probe(input, job.Token);
            if (ext is not ("png" or "jpg" or "bmp" or "gif" or "tiff") || Math.Max(info.Width, info.Height) > 4000)
            {
                temp = Path.Combine(Path.GetTempPath(), $"luna-ocr-{Guid.NewGuid():N}.png");
                Ffmpeg.Run(job, new[]
                {
                    "-i", input, "-frames:v", "1", "-update", "1",
                    "-vf", "scale='min(4000,iw)':'min(4000,ih)':force_original_aspect_ratio=decrease",
                }, temp, record: false);
                source = temp;
            }
            var text = WinRt.Ocr(source, job.Token);
            if (string.IsNullOrWhiteSpace(text)) throw new ToolException("No text was recognized in this image.");
            job.ResultText = text;
        }
        finally { if (temp != null) Paths.TryDelete(temp); }
    }

    public static void StripMetadata(Job job, string input)
    {
        var ext = Norm(Paths.Ext(input));
        var output = Paths.Out(input, Paths.Ext(input), "clean");
        if (MetadataOps.TryStripLossless(input, output)) { job.Outputs.Add(output); return; }
        if (!Writable.Contains(ext)) throw new ToolException($"Can't rewrite .{ext} files without metadata.");
        Encode(job, input, output, ext, null, compress: false);
    }

    /// <summary>The format a tool should write: the input's own, or a sensible stand-in for read-only formats.</summary>
    static string KeepFormat(string input)
    {
        var ext = Norm(Paths.Ext(input));
        if (Writable.Contains(ext)) return ext;
        return ext is "heic" ? "jpg" : "png";
    }

    static void Encode(Job job, string input, string output, string fmt, string? pre, bool compress)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        // ffprobe can't count GIF/WebP frames cheaply, so animation-capable pairs always keep every frame.
        bool keepAnimation = Paths.Ext(input) is "gif" or "webp" or "apng" && fmt is "gif" or "webp";

        if (fmt == "avif" && info.HasAlpha)
        {
            EncodeAvifWithAlpha(job, input, output, pre, compress);
            return;
        }

        var args = new List<string> { "-i", input };
        var chain = new List<string>();
        if (pre != null) chain.Add(pre);
        switch (fmt)
        {
            case "jpg":
                if (info.HasAlpha) chain.Add(Flatten);
                args.AddRange(new[] { "-c:v", "mjpeg", "-q:v", compress ? "5" : "2" });
                break;
            case "png":
                if (compress)
                    chain.Add("split[pa][pb];[pa]palettegen=max_colors=256:reserve_transparent=1[pp];[pb][pp]paletteuse=dither=sierra2_4a");
                args.AddRange(new[] { "-c:v", "png", "-pred", "mixed" });
                break;
            case "webp":
                args.AddRange(keepAnimation ? new[] { "-c:v", "libwebp_anim", "-loop", "0" } : new[] { "-c:v", "libwebp" });
                args.AddRange(new[] { "-quality", compress ? "70" : "90" });
                break;
            case "avif":
                args.AddRange(new[] { "-c:v", "libaom-av1", "-still-picture", "1", "-crf", compress ? "40" : "28", "-cpu-used", "6", "-row-mt", "1", "-pix_fmt", "yuv420p" });
                break;
            case "jxl":
                args.AddRange(new[] { "-c:v", "libjxl", "-distance", compress ? "2.5" : "1" });
                break;
            case "gif":
                chain.Add($"split[ga][gb];[ga]palettegen=max_colors={(compress ? 96 : 256)}:reserve_transparent=1" +
                          (keepAnimation ? ":stats_mode=diff" : "") +
                          $"[gp];[gb][gp]paletteuse=dither={(compress ? "bayer:bayer_scale=4" : "sierra2_4a")}:alpha_threshold=128");
                if (keepAnimation) args.AddRange(new[] { "-loop", "0" });
                break;
            case "bmp":
                args.AddRange(new[] { "-c:v", "bmp" });
                break;
            case "tiff":
                // Plain RGB(A): YCbCr TIFFs open in almost no viewers.
                args.AddRange(new[] { "-c:v", "tiff", "-compression_algo", "lzw", "-pix_fmt", info.HasAlpha ? "rgba" : "rgb24" });
                break;
            default:
                throw new ToolException($"Can't write .{fmt} images.");
        }
        if (!keepAnimation) args.AddRange(new[] { "-frames:v", "1" });
        if (fmt is "png" or "jpg" or "bmp" or "tiff" or "jxl") args.AddRange(new[] { "-update", "1" });
        if (chain.Count > 0) args.AddRange(new[] { "-vf", string.Join(",", chain) });
        Ffmpeg.Run(job, args, output);
    }

    static void EncodeAvifWithAlpha(Job job, string input, string output, string? pre, bool compress)
    {
        var graph = $"[0:v]{(pre != null ? pre + "," : "")}format=yuva444p,split[c][a];[c]format=yuv444p[cc];[a]alphaextract[aa]";
        Ffmpeg.Run(job, new[]
        {
            "-i", input, "-filter_complex", graph, "-map", "[cc]", "-map", "[aa]",
            "-c:v", "libaom-av1", "-still-picture", "1", "-crf", compress ? "40" : "28", "-cpu-used", "6", "-row-mt", "1",
            "-frames:v", "1",
        }, output);
    }

    static readonly int[] IconSizes = { 256, 64, 48, 32, 24, 16 };

    /// <summary>Multi-resolution .ico: the image is centered on a transparent square first.</summary>
    static void MakeIcon(Job job, string input, string output)
    {
        var splits = string.Concat(IconSizes.Select((_, i) => $"[s{i}]"));
        var graph = "[0:v]format=rgba,scale=256:256:force_original_aspect_ratio=decrease:flags=lanczos," +
                    $"pad=256:256:(ow-iw)/2:(oh-ih)/2:color=black@0,split={IconSizes.Length}{splits}";
        for (int i = 0; i < IconSizes.Length; i++)
            graph += $";[s{i}]scale={IconSizes[i]}:{IconSizes[i]}:flags=lanczos[o{i}]";
        var args = new List<string> { "-i", input, "-filter_complex", graph };
        for (int i = 0; i < IconSizes.Length; i++) args.AddRange(new[] { "-map", $"[o{i}]" });
        args.AddRange(new[] { "-c:v", "png", "-frames:v", "1", "-f", "ico" });
        Ffmpeg.Run(job, args, output);
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };
}
