using System.IO.Compression;
using Luna.Engines;

namespace Luna.Ops;

internal static class ArchiveOps
{
    static string Tar => Tools.Tar ?? throw new ToolException("Windows tar was not found.");

    /// <summary>Extracts next to the archive. A single top-level item is placed directly, like macOS does.</summary>
    public static void Extract(Job job, string archive)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(archive))!;
        var temp = Path.Combine(dir, $".luna-{Guid.NewGuid():N}");
        try
        {
            ExtractTo(job, archive, temp);
            var entries = Directory.GetFileSystemEntries(temp);
            string result;
            if (entries.Length == 1)
            {
                result = Paths.Unique(Path.Combine(dir, Path.GetFileName(entries[0])));
                Move(entries[0], result);
            }
            else
            {
                result = Paths.OutDir(archive);
                Directory.Move(temp, result);
            }
            job.Outputs.Add(result);
        }
        finally { Paths.TryDelete(temp); }
    }

    static void ExtractTo(Job job, string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        File.SetAttributes(dest, FileAttributes.Hidden | FileAttributes.Directory);
        try { Proc.Run(Tar, new[] { "-xf", archive, "-C", dest }, job.Token); }
        catch (ToolException) when (Paths.Ext(archive) == "gz" && !archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            // A plain gzip file (not a tarball): just decompress it.
            foreach (var e in Directory.GetFileSystemEntries(dest)) Paths.TryDelete(e);
            using var src = File.OpenRead(archive);
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            using var dst = File.Create(Path.Combine(dest, Path.GetFileNameWithoutExtension(archive)));
            gz.CopyTo(dst);
        }
        File.SetAttributes(dest, FileAttributes.Directory);
    }

    static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }

    public static void Pack(Job job, IReadOnlyList<string> items, string fmt)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(items[0].TrimEnd('\\')))!;
        var output = items.Count == 1 ? Paths.Out(items[0], fmt) : Paths.Unique(Path.Combine(dir, "Archive." + fmt));
        PackInto(job, dir, items.Select(i => Path.GetFileName(i.TrimEnd('\\'))).ToList(), output, fmt);
        job.Outputs.Add(output);
    }

    public static void Convert(Job job, string archive, string fmt)
    {
        var temp = Paths.TempDir();
        try
        {
            ExtractTo(job, archive, temp);
            var names = Directory.GetFileSystemEntries(temp).Select(Path.GetFileName).ToList();
            var output = Paths.Out(archive, fmt);
            PackInto(job, temp, names!, output, fmt);
            job.Outputs.Add(output);
        }
        finally { Paths.TryDelete(temp); }
    }

    static void PackInto(Job job, string root, IReadOnlyList<string> names, string output, string fmt)
    {
        try
        {
            if (fmt == "zip") Zip(job, root, names, output);
            else Proc.Run(Tar, new[] { "-a", "-cf", output, "-C", root }.Concat(names), job.Token, workDir: root);
        }
        catch
        {
            Paths.TryDelete(output);
            throw;
        }
    }

    /// <summary>.NET's zip writer marks names as UTF-8, so non-English file names survive on every OS.</summary>
    static void Zip(Job job, string root, IReadOnlyList<string> names, string output)
    {
        var files = new List<(string full, string entry)>();
        var emptyDirs = new List<string>();
        foreach (var name in names)
        {
            var full = Path.Combine(root, name);
            if (File.Exists(full)) { files.Add((full, name)); continue; }
            foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                files.Add((f, Path.GetRelativePath(root, f).Replace('\\', '/')));
            foreach (var d in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).Prepend(full))
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    emptyDirs.Add(Path.GetRelativePath(root, d).Replace('\\', '/') + "/");
        }
        long total = Math.Max(1, files.Sum(f => new FileInfo(f.full).Length)), done = 0;
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var (full, entry) in files)
        {
            job.Token.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(full, entry, CompressionLevel.Optimal);
            done += new FileInfo(full).Length;
            job.Report((double)done / total);
        }
        foreach (var d in emptyDirs) zip.CreateEntry(d);
    }
}
