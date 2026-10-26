using Luna.Engines;
using Luna.Ops;

namespace Luna;

internal enum Kind { Image, Video, Audio, Pdf, Word, Text, Markdown, Html, Slides, Sheet, Archive, Folder, Other }

/// <summary>One menu entry: a target format (chip) or a tool (row).</summary>
internal sealed record Act(string Id, string Label, string Glyph, Need Needs, string Verb)
{
    public Action<Job, string>? PerFile { get; init; }
    public Action<Job, IReadOnlyList<string>>? Batch { get; init; }
    public bool IsFormat { get; init; }
    /// <summary>Removes the user's files (shown in red on the ring).</summary>
    public bool Destructive { get; init; }
    /// <summary>Compact uppercase label for a ring slice.</summary>
    public string Short => ShortLabels.TryGetValue(Id, out var s) ? s : Label.ToUpperInvariant();

    static readonly Dictionary<string, string> ShortLabels = new()
    {
        ["half"] = "50%", ["fit"] = "FIT 1920", ["rotr"] = "RIGHT 90°", ["rotl"] = "LEFT 90°", ["rotate"] = "RIGHT 90°",
        ["meta"] = "METADATA", ["qr"] = "QR CODE", ["combine"] = "TO PDF", ["720p"] = "720P", ["mute"] = "MUTE",
        ["loud"] = "NORMALIZE", ["trim"] = "TRIM", ["mono"] = "MONO", ["merge"] = "MERGE", ["split"] = "SPLIT",
        ["unlock"] = "UNLOCK", ["extract"] = "EXTRACT", ["zip"] = "ZIP", ["delete"] = "DELETE",
    };
}

internal sealed class MenuModel
{
    public required IReadOnlyList<string> Files { get; init; }
    public required List<Act> Formats { get; init; }
    public required List<Act> Tools { get; init; }
}

internal static class Catalog
{
    static readonly Dictionary<string, Kind> Kinds = new(StringComparer.OrdinalIgnoreCase);

    static Catalog()
    {
        void Add(Kind k, string exts) { foreach (var e in exts.Split(' ')) Kinds[e] = k; }
        Add(Kind.Image, "png jpg jpeg jfif jpe webp avif heic heif bmp dib tif tiff gif ico jxl psd tga qoi apng");
        Add(Kind.Video, "mp4 m4v mkv mov avi webm wmv flv mpg mpeg ts mts m2ts 3gp ogv vob");
        Add(Kind.Audio, "mp3 wav flac m4a aac ogg oga opus wma aiff aif amr ac3 mka caf weba");
        Add(Kind.Pdf, "pdf");
        Add(Kind.Word, "doc docx docm dot dotx rtf odt");
        Add(Kind.Text, "txt log");
        Add(Kind.Markdown, "md markdown");
        Add(Kind.Html, "html htm");
        Add(Kind.Slides, "ppt pptx pptm pps ppsx odp pot potx");
        Add(Kind.Sheet, "xls xlsx xlsm xlsb ods csv");
        Add(Kind.Archive, "zip 7z rar tar gz tgz bz2 tbz tbz2 xz txz zst tzst iso cab lzh lha");
    }

    public static Kind KindOf(string path) =>
        Directory.Exists(path) ? Kind.Folder : Kinds.GetValueOrDefault(Paths.Ext(path), Kind.Other);

    public static MenuModel Build(IReadOnlyList<string> allFiles)
    {
        var files = allFiles.Where(f => File.Exists(f) || Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) files = allFiles.ToList();
        var main = files.GroupBy(KindOf).OrderByDescending(g => g.Count()).First();
        var kind = main.Key;
        var picked = main.ToList();

        var formats = Formats(kind, picked);
        var tools = Tools(kind, picked);
        if (kind is not (Kind.Archive or Kind.Folder or Kind.Other))
            tools.Add(ZipTool(files));
        tools.Add(DeleteTool(files));

        return new MenuModel
        {
            Files = picked,
            Formats = formats,
            Tools = tools,
        };
    }

    static List<Act> Formats(Kind kind, List<string> files)
    {
        var own = files.Select(f => f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || Paths.Ext(f) == "tgz"
            ? "tar.gz" : ImageOps.Norm(Paths.Ext(f))).ToHashSet();
        // When every selected file already has a format, don't offer converting to it.
        bool NotOwn(string ext) => !(own.Count == 1 && own.Contains(ext));

        IEnumerable<Act> list = kind switch
        {
            Kind.Image => new[] { "png", "jpg", "webp", "avif", "gif", "bmp", "tiff", "ico", "jxl" }
                .Select(e => Fmt(e, Need.Ffmpeg, ImageOps.Convert))
                .Append(Fmt("pdf", Need.Ffmpeg, ImageOps.Convert)),
            Kind.Video => new[] { "mp4", "mov", "mkv", "webm", "avi", "gif" }.Select(e => Fmt(e, Need.Ffmpeg, MediaOps.ToVideo))
                .Concat(new[] { "mp3", "m4a", "wav" }.Select(e => Fmt(e, Need.Ffmpeg, MediaOps.ToAudio))),
            Kind.Audio => new[] { "mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff" }.Select(e => Fmt(e, Need.Ffmpeg, MediaOps.ToAudio)),
            Kind.Pdf => new[]
            {
                Fmt("docx", Need.Word, DocOps.WordConvert),
                Fmt("png", Need.None, PdfOps.ToImages),
                Fmt("jpg", Need.None, PdfOps.ToImages),
                Fmt("txt", Need.Word, DocOps.WordConvert),
            },
            Kind.Word => new[] { "pdf", "docx", "doc", "odt", "rtf", "txt", "html" }.Select(e => Fmt(e, Need.Word, DocOps.WordConvert)),
            Kind.Text => new[] { Fmt("pdf", Need.Edge, DocOps.TextConvert), Fmt("docx", Need.Word, DocOps.TextConvert) },
            Kind.Markdown => new[]
            {
                Fmt("html", Need.None, DocOps.MarkdownConvert),
                Fmt("pdf", Need.Edge, DocOps.MarkdownConvert),
                Fmt("docx", Need.Word, DocOps.MarkdownConvert),
            },
            Kind.Html => new[] { Fmt("pdf", Need.Edge, DocOps.HtmlConvert), Fmt("docx", Need.Word, DocOps.HtmlConvert), Fmt("txt", Need.Word, DocOps.HtmlConvert) },
            Kind.Slides => new[] { "pdf", "pptx", "ppt", "odp", "png", "jpg", "mp4" }.Select(e => Fmt(e, Need.PowerPoint, DocOps.PowerPointConvert)),
            Kind.Sheet => new[] { "pdf", "xlsx", "xls", "csv", "ods" }.Select(e => Fmt(e, Need.Excel, DocOps.ExcelConvert)),
            Kind.Archive => new[] { "zip", "7z", "tar.gz" }.Select(e => Fmt(e, Need.Tar, ArchiveOps.Convert)),
            _ => new[] { "zip", "7z", "tar.gz" }.Select(e => new Act($"pack-{e}", e.ToUpperInvariant(), "", e == "zip" ? Need.None : Need.Tar, "Compressing")
            {
                IsFormat = true,
                Batch = (job, items) => ArchiveOps.Pack(job, items, e),
            }),
        };
        return list.Where(a => !a.IsFormat || a.Batch != null || NotOwn(a.Id[3..])).ToList();
    }

    static List<Act> Tools(Kind kind, List<string> files)
    {
        var t = new List<Act>();
        bool many = files.Count > 1;
        switch (kind)
        {
            case Kind.Image:
                t.Add(Tool("compress", "Compress", "\uE73F", Need.Ffmpeg, "Compressing", ImageOps.Compress));
                t.Add(Tool("half", "Resize 50%", "\uE71F", Need.Ffmpeg, "Resizing", (j, f) => ImageOps.Transform(j, f, "scale=trunc(iw/2):trunc(ih/2):flags=lanczos", "50%")));
                t.Add(Tool("fit", "Fit 1920 px", "\uE740", Need.Ffmpeg, "Resizing",
                    (j, f) => ImageOps.Transform(j, f, "scale=w='min(1920,iw)':h='min(1920,ih)':force_original_aspect_ratio=decrease:flags=lanczos", "1920")));
                t.Add(Tool("rotr", "Rotate right", "\uE7AD", Need.Ffmpeg, "Rotating", (j, f) => ImageOps.Transform(j, f, "transpose=1", "rotated")));
                t.Add(Tool("rotl", "Rotate left", "\uE7A7", Need.Ffmpeg, "Rotating", (j, f) => ImageOps.Transform(j, f, "transpose=2", "rotated")));
                t.Add(Tool("flip", "Flip", "\uE8AB", Need.Ffmpeg, "Flipping", (j, f) => ImageOps.Transform(j, f, "hflip", "flipped")));
                t.Add(Tool("gray", "Grayscale", "\uE790", Need.Ffmpeg, "Converting", (j, f) => ImageOps.Transform(j, f, "hue=s=0", "grayscale")));
                t.Add(Tool("meta", "Remove metadata", "\uE75C", Need.Ffmpeg, "Removing metadata", ImageOps.StripMetadata));
                t.Add(Tool("qr", "Read QR code", "\uED14", Need.Ffmpeg, "Reading QR code", ImageOps.ReadQr));
                t.Add(Tool("ocr", "Copy text", "\uE8C1", Need.Ffmpeg, "Reading text", ImageOps.Ocr));
                if (many) t.Add(BatchTool("combine", "Combine into PDF", "\uE71B", Need.Ffmpeg, "Combining",
                    (j, items) => PdfWriter.FromImages(j, items.OrderBy(i => i, Comparer<string>.Create(Paths.Logical)).ToList(),
                        Paths.Unique(Path.Combine(Path.GetDirectoryName(items[0])!, "Combined.pdf")))));
                break;
            case Kind.Video:
                t.Add(Tool("compress", "Compress", "\uE73F", Need.Ffmpeg, "Compressing", (j, f) => MediaOps.Compress(j, f, 1080, 28, "compressed")));
                t.Add(Tool("720p", "Resize to 720p", "\uE7F4", Need.Ffmpeg, "Resizing", (j, f) => MediaOps.Compress(j, f, 720, 23, "720p")));
                t.Add(Tool("rotate", "Rotate right", "\uE7AD", Need.Ffmpeg, "Rotating", MediaOps.Rotate));
                t.Add(Tool("mute", "Remove audio", "\uE74F", Need.Ffmpeg, "Removing audio", MediaOps.Mute));
                t.Add(Tool("thumb", "Thumbnail", "\uE722", Need.Ffmpeg, "Grabbing a frame", MediaOps.Thumbnail));
                t.Add(Tool("meta", "Remove metadata", "\uE75C", Need.Ffmpeg, "Removing metadata", MediaOps.StripMetadata));
                break;
            case Kind.Audio:
                t.Add(Tool("loud", "Normalize volume", "\uE767", Need.Ffmpeg, "Normalizing", (j, f) => MediaOps.AudioFilter(j, f, "loudnorm=I=-16:TP=-1.5:LRA=11", "normalized")));
                t.Add(Tool("trim", "Trim silence", "\uE8C6", Need.Ffmpeg, "Trimming",
                    (j, f) => MediaOps.AudioFilter(j, f, "silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse", "trimmed")));
                t.Add(Tool("mono", "Make mono", "\uE7F5", Need.Ffmpeg, "Converting", (j, f) => MediaOps.AudioFilter(j, f, null, "mono", mono: true)));
                t.Add(Tool("meta", "Remove metadata", "\uE75C", Need.Ffmpeg, "Removing metadata", MediaOps.StripAudioMetadata));
                break;
            case Kind.Pdf:
                if (many) t.Add(BatchTool("merge", "Merge PDFs", "\uE71B", Need.Qpdf, "Merging", PdfOps.Merge));
                t.Add(Tool("compress", "Compress", "\uE73F", Need.Qpdf, "Compressing", PdfOps.Compress));
                t.Add(Tool("split", "Split pages", "\uE8C6", Need.Qpdf, "Splitting", PdfOps.Split));
                t.Add(Tool("rotate", "Rotate right", "\uE7AD", Need.Qpdf, "Rotating", PdfOps.Rotate));
                t.Add(Tool("unlock", "Remove restrictions", "\uE785", Need.Qpdf, "Unlocking", PdfOps.Unlock));
                break;
            case Kind.Word:
                t.Add(Tool("meta", "Remove metadata", "\uE75C", Need.Word, "Removing metadata", DocOps.WordStripMetadata));
                break;
            case Kind.Sheet when files.All(f => Paths.Ext(f) != "csv"):
                t.Add(Tool("meta", "Remove metadata", "\uE75C", Need.Excel, "Removing metadata", DocOps.ExcelStripMetadata));
                break;
            case Kind.Slides:
                t.Add(Tool("meta", "Remove metadata", "\uE75C", Need.PowerPoint, "Removing metadata", DocOps.PowerPointStripMetadata));
                break;
            case Kind.Archive:
                t.Add(Tool("extract", "Extract here", "\uE838", Need.Tar, "Extracting", ArchiveOps.Extract));
                break;
        }
        return t;
    }

    static Act DeleteTool(List<string> files) =>
        new("delete", "Move to Recycle Bin", "", Need.None, "Moving to Recycle Bin")
        {
            Batch = (j, _) => DeleteOps.ToRecycleBin(j, files),
            Destructive = true,
        };

    static Act ZipTool(List<string> files) =>
        BatchTool("zip", files.Count == 1 ? "Zip" : $"Zip {files.Count} items", "\uF012", Need.None, "Compressing",
            (j, _) => ArchiveOps.Pack(j, files, "zip"));

    static Act Fmt(string ext, Need need, Action<Job, string, string> run) =>
        new($"to-{ext}", ext.ToUpperInvariant(), "", need, $"Converting to {ext.ToUpperInvariant()}")
        {
            IsFormat = true,
            PerFile = (job, file) => run(job, file, ext),
        };

    static Act Tool(string id, string label, string glyph, Need need, string verb, Action<Job, string> run) =>
        new(id, label, glyph, need, verb) { PerFile = run };

    static Act BatchTool(string id, string label, string glyph, Need need, string verb, Action<Job, IReadOnlyList<string>> run) =>
        new(id, label, glyph, need, verb) { Batch = run };
}
