using Luna.Engines;

namespace Luna.Ops;

internal static class MediaOps
{
    const string Even = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
    static readonly string[] AudioWritable = { "mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "aiff", "wma" };

    // ---------- video → video ----------

    public static void ToVideo(Job job, string input, string fmt)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        if (!info.HasVideo) throw new ToolException("This file has no video track.");
        var output = Paths.Out(input, fmt);
        if (fmt == "gif")
        {
            Ffmpeg.Run(job, new[]
            {
                "-i", input, "-an", "-loop", "0", "-vf",
                "fps=12,scale='min(640,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];" +
                "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
            }, output, info.Duration);
            return;
        }

        // Remuxing is instant and lossless, so try stream copy before re-encoding.
        var attempts = new List<List<string>>();
        if (fmt == "mkv") attempts.Add(new() { "-i", input, "-map", "0", "-c", "copy" });
        if (CanCopy(info, fmt))
        {
            var copy = new List<string> { "-i", input, "-map", "0:v:0", "-map", "0:a?", "-c", "copy" };
            if (fmt is "mp4" or "mov" or "m4v")
            {
                copy.AddRange(new[] { "-movflags", "+faststart" });
                if (info.VCodec == "hevc") copy.AddRange(new[] { "-tag:v", "hvc1" });
            }
            attempts.Add(copy);
        }
        attempts.Add(EncodeArgs(input, fmt, crf: null, maxShortSide: 0));

        for (int i = 0; i < attempts.Count; i++)
        {
            try
            {
                Ffmpeg.Run(job, attempts[i], output, info.Duration);
                return;
            }
            catch (ToolException) when (i < attempts.Count - 1) { job.Token.ThrowIfCancellationRequested(); }
        }
    }

    public static void Compress(Job job, string input, int maxShortSide, int crf, string tag)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        if (!info.HasVideo) throw new ToolException("This file has no video track.");
        if (tag == "720p" && Math.Min(info.Width, info.Height) <= maxShortSide)
        {
            job.Note = $"{Path.GetFileName(input)} is already {Math.Min(info.Width, info.Height)}p.";
            return;
        }
        var output = Paths.Out(input, "mp4", tag);
        Ffmpeg.Run(job, EncodeArgs(input, "mp4", crf, maxShortSide), output, info.Duration);
        long before = new FileInfo(input).Length, after = new FileInfo(output).Length;
        job.Note = $"{ImageOps.Size(before)} → {ImageOps.Size(after)}";
    }

    public static void Mute(Job job, string input)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        Ffmpeg.Run(job, new[] { "-i", input, "-map", "0", "-map", "-0:a", "-c", "copy" },
            Paths.Out(input, Paths.Ext(input), "muted"), info.Duration);
    }

    public static void Rotate(Job job, string input)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        var ext = Paths.Ext(input) is "mp4" or "mov" or "mkv" or "m4v" ? Paths.Ext(input) : "mp4";
        var args = EncodeArgs(input, ext, crf: 18, maxShortSide: 0);
        int vf = args.IndexOf("-vf");
        args[vf + 1] = "transpose=1," + args[vf + 1];
        Ffmpeg.Run(job, args, Paths.Out(input, ext, "rotated"), info.Duration);
    }

    public static void Thumbnail(Job job, string input)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        var at = Math.Max(0, Math.Min(info.Duration * 0.1, 10));
        Ffmpeg.Run(job, new[]
        {
            "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", input,
            "-vf", "thumbnail=30", "-frames:v", "1", "-update", "1", "-q:v", "2",
        }, Paths.Out(input, "jpg", "thumbnail"));
    }

    public static void StripMetadata(Job job, string input)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        Ffmpeg.Run(job, new[]
        {
            "-i", input, "-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy",
            "-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1",
            "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact",
        }, Paths.Out(input, Paths.Ext(input), "clean"), info.Duration);
    }

    static bool CanCopy(MediaInfo info, string fmt)
    {
        string? v = info.VCodec, a = info.ACodec;
        return fmt switch
        {
            "mp4" or "m4v" => v is "h264" or "hevc" or "av1" or "mpeg4" && a is null or "aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus" or "flac",
            "mov" => v is "h264" or "hevc" or "prores" or "mjpeg" or "mpeg4" && a is null or "aac" or "mp3" or "alac" or "pcm_s16le" or "pcm_s24le" or "ac3",
            "webm" => v is "vp8" or "vp9" or "av1" && a is null or "opus" or "vorbis",
            "avi" => v is "mpeg4" or "h264" or "mjpeg" && a is null or "mp3" or "ac3" or "pcm_s16le",
            _ => false,
        };
    }

    static List<string> EncodeArgs(string input, string fmt, int? crf, int maxShortSide)
    {
        var vf = maxShortSide > 0
            ? $"scale=w='if(gte(iw,ih),-2,min(iw,{maxShortSide}))':h='if(gte(iw,ih),min(ih,{maxShortSide}),-2)',{Even}"
            : Even;
        var args = new List<string> { "-i", input, "-map", "0:v:0", "-map", "0:a?", "-vf", vf };
        switch (fmt)
        {
            case "webm":
                args.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", (crf ?? 32).ToString(), "-b:v", "0", "-row-mt", "1",
                    "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k" });
                break;
            case "avi":
                args.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "3" });
                break;
            default:
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", (crf ?? 20).ToString(), "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", crf >= 26 ? "128k" : "192k" });
                if (fmt is "mp4" or "mov" or "m4v") args.AddRange(new[] { "-movflags", "+faststart" });
                break;
        }
        return args;
    }

    // ---------- anything → audio ----------

    public static void ToAudio(Job job, string input, string fmt)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        if (!info.HasAudio) throw new ToolException("This file has no audio track.");
        var args = new List<string> { "-i", input, "-map", "0:a:0", "-vn" };
        bool copy = (info.ACodec, fmt) is ("aac", "m4a" or "aac") or ("mp3", "mp3") or ("flac", "flac")
            or ("opus", "opus") or ("vorbis", "ogg");
        args.AddRange(copy ? new[] { "-c:a", "copy" } : AudioCodec(fmt));
        Ffmpeg.Run(job, args, Paths.Out(input, fmt), info.Duration);
    }

    /// <summary>Applies an audio filter and re-encodes to the same format (or M4A for read-only formats).</summary>
    public static void AudioFilter(Job job, string input, string? filter, string tag, bool mono = false)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        if (!info.HasAudio) throw new ToolException("This file has no audio track.");
        var ext = AudioWritable.Contains(Paths.Ext(input)) ? Paths.Ext(input) : "m4a";
        var args = new List<string> { "-i", input, "-map", "0:a:0", "-vn" };
        if (filter != null) args.AddRange(new[] { "-af", filter });
        if (mono) args.AddRange(new[] { "-ac", "1" });
        if (info.SampleRate > 0 && ext != "opus") args.AddRange(new[] { "-ar", info.SampleRate.ToString() });
        args.AddRange(AudioCodec(ext));
        Ffmpeg.Run(job, args, Paths.Out(input, ext, tag), info.Duration);
    }

    public static void StripAudioMetadata(Job job, string input)
    {
        var info = Ffmpeg.Probe(input, job.Token);
        Ffmpeg.Run(job, new[]
        {
            "-i", input, "-map", "0:a", "-c", "copy", "-map_metadata", "-1", "-map_metadata:s", "-1",
            "-fflags", "+bitexact", "-flags:a", "+bitexact",
        }, Paths.Out(input, Paths.Ext(input), "clean"), info.Duration);
    }

    static string[] AudioCodec(string fmt) => fmt switch
    {
        "mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
        "m4a" or "aac" => new[] { "-c:a", "aac", "-b:a", "192k" },
        "wav" => new[] { "-c:a", "pcm_s16le" },
        "flac" => new[] { "-c:a", "flac" },
        "ogg" => new[] { "-c:a", "libvorbis", "-q:a", "6" },
        "opus" => new[] { "-c:a", "libopus", "-b:a", "128k" },
        "aiff" => new[] { "-c:a", "pcm_s16be" },
        "wma" => new[] { "-c:a", "wmav2", "-b:a", "192k" },
        _ => throw new ToolException($"Can't write .{fmt} audio."),
    };

}
