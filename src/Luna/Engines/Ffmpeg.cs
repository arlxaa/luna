using System.Globalization;
using System.Text.Json;

namespace Luna.Engines;

internal sealed record MediaInfo(
    double Duration, bool HasVideo, bool HasAudio, string? VCodec, string? ACodec,
    int Width, int Height, string? PixFmt, int Frames, int SampleRate)
{
    public bool HasAlpha => PixFmt is { } f && (f.StartsWith("yuva") || f.StartsWith("ya") || f.StartsWith("gbrap")
        || f.Contains("rgba") || f.Contains("bgra") || f.Contains("argb") || f.Contains("abgr") || f == "pal8");
}

internal static class Ffmpeg
{
    static string Exe => Tools.Ffmpeg ?? throw new ToolException("FFmpeg is not installed.");

    public static MediaInfo Probe(string input, CancellationToken ct)
    {
        var probe = Tools.Ffprobe ?? throw new ToolException("ffprobe was not found next to FFmpeg.");
        var json = Proc.Run(probe, new[]
        {
            "-v", "error", "-print_format", "json",
            "-show_entries", "stream=codec_type,codec_name,width,height,pix_fmt,nb_frames,sample_rate:stream_disposition=attached_pic:format=duration",
            input,
        }, ct, timeoutSeconds: 60);
        using var doc = JsonDocument.Parse(json);
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        string? vcodec = null, acodec = null, pix = null;
        int w = 0, h = 0, frames = 0, rate = 0;
        bool video = false, audio = false;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                bool cover = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && !video && !cover)
                {
                    video = true;
                    vcodec = Str(s, "codec_name");
                    pix = Str(s, "pix_fmt");
                    w = Int(s, "width"); h = Int(s, "height");
                    frames = int.TryParse(Str(s, "nb_frames"), out var nf) ? nf : 0;
                }
                else if (type == "audio" && !audio)
                {
                    audio = true;
                    acodec = Str(s, "codec_name");
                    rate = int.TryParse(Str(s, "sample_rate"), out var sr) ? sr : 0;
                }
            }
        }
        return new MediaInfo(duration, video, audio, vcodec, acodec, w, h, pix, frames, rate);

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : null;
        static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    }

    /// <summary>Runs ffmpeg writing to <paramref name="output"/>; deletes the partial file on failure.</summary>
    public static void Run(Job job, IEnumerable<string> args, string output, double duration = 0, bool record = true)
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);
        all.Add(output);
        try
        {
            Proc.Run(Exe, all, job.Token, line =>
            {
                if (duration > 0 && line.StartsWith("out_time_us=") &&
                    long.TryParse(line.AsSpan(12), out long us) && us > 0)
                    job.Report(us / 1e6 / duration);
            });
        }
        catch
        {
            Paths.TryDelete(output);
            throw;
        }
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            Paths.TryDelete(output);
            throw new ToolException("FFmpeg produced no output for this file.");
        }
        if (record) job.Outputs.Add(output);
    }

    /// <summary>Runs ffmpeg and returns raw stdout bytes (for piping frames into the PDF writer).</summary>
    public static byte[] Pipe(Job job, IEnumerable<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error" }.Concat(args).Append("pipe:1"))
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        using (job.Token.Register(() => { try { p.Kill(true); } catch { } }))
            p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        job.Token.ThrowIfCancellationRequested();
        if (p.ExitCode != 0 || ms.Length == 0)
            throw new ToolException("FFmpeg failed: " + errTask.Result.Trim().Split('\n').LastOrDefault());
        return ms.ToArray();
    }

    /// <summary>Lines of text that the quirc filter decoded from QR codes in an image.</summary>
    public static List<string> ReadQr(Job job, string input)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"luna-qr-{Guid.NewGuid():N}.txt");
        try
        {
            // quirc needs a quiet zone; pad generously and downscale huge photos.
            var filter = "scale='min(2000,iw)':-1,format=gray,pad=iw+80:ih+80:40:40:white,quirc," +
                         $"metadata=mode=print:file='{tmp.Replace("\\", "/").Replace(":", "\\:")}'";
            Proc.Run(Exe, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-frames:v", "1", "-vf", filter, "-f", "null", "-" }, job.Token);
            if (!File.Exists(tmp)) return new();
            return File.ReadAllLines(tmp)
                .Where(l => l.StartsWith("lavfi.quirc.") && l.Contains(".payload="))
                .Select(l => l[(l.IndexOf(".payload=", StringComparison.Ordinal) + 9)..])
                .Distinct().ToList();
        }
        finally { Paths.TryDelete(tmp); }
    }
}
