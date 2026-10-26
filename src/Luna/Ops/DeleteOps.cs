using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Luna.Engines;

namespace Luna.Ops;

/// <summary>
/// Moves files to the Recycle Bin (never a silent permanent delete) and can put them back,
/// like "Move to Trash" + Undo on macOS.
/// </summary>
internal static class DeleteOps
{
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>One item sitting in the Recycle Bin: where it came from and its $R / $I files.</summary>
    sealed record Recycled(string Original, string Stored, string Info);

    public static void ToRecycleBin(Job job, IReadOnlyList<string> items)
    {
        var paths = items.Select(p => Path.GetFullPath(p.TrimEnd('\\'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var since = DateTime.UtcNow.AddSeconds(-2);
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            // Recycle silently; Windows still asks first if an item can't be recycled
            // (network drives, huge files) and would be deleted permanently.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT,
        };
        int rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) throw new OperationCanceledException();
        if (rc != 0) throw new ToolException($"Windows couldn't delete {(paths.Count == 1 ? "this item" : "these items")} (code 0x{rc:X}). Is it open in another app?");

        var recycled = FindRecycled(paths, since);
        for (int i = 0; i < 5 && recycled.Count < paths.Count; i++)
        {
            Thread.Sleep(100); // the shell can finish writing $I records a moment later
            recycled = FindRecycled(paths, since);
        }
        job.Note = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} items";
        if (recycled.Count > 0) job.Undo = () => Restore(recycled);
    }

    /// <summary>Puts recycled items back where they were (next to them if the name is taken now).</summary>
    static int Restore(List<Recycled> items)
    {
        int restored = 0;
        foreach (var item in items)
        {
            try
            {
                bool isDir = Directory.Exists(item.Stored);
                if (!isDir && !File.Exists(item.Stored)) continue; // emptied or restored by hand
                var target = File.Exists(item.Original) || Directory.Exists(item.Original) ? Paths.Unique(item.Original) : item.Original;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Antivirus or the thumbnail indexer may hold a just-recycled file for a moment.
                Retry(() => { if (isDir) Directory.Move(item.Stored, target); else File.Move(item.Stored, target); });
                Retry(() => File.Delete(item.Info));
                restored++;
            }
            catch { }
        }
        return restored;
    }

    static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(150); }
        }
    }

    /// <summary>
    /// Finds our items in this user's Recycle Bin. Each deleted item is stored as $R&lt;id&gt; next to
    /// a $I&lt;id&gt; record holding its original path.
    /// </summary>
    static List<Recycled> FindRecycled(List<string> originals, DateTime sinceUtc)
    {
        var found = new List<Recycled>();
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return found;
        var wanted = originals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in originals.Select(Path.GetPathRoot).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = Path.Combine(root ?? "", "$Recycle.Bin", sid);
            if (!Directory.Exists(bin)) continue;
            try
            {
                foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
                {
                    if (File.GetLastWriteTimeUtc(info) < sinceUtc) continue;
                    var original = OriginalPath(info);
                    if (original == null || !wanted.Contains(original)) continue;
                    var stored = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                    if (File.Exists(stored) || Directory.Exists(stored)) found.Add(new Recycled(original, stored, info));
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>Reads the original path from a $I record (version 1: fixed 260 chars; version 2: length-prefixed).</summary>
    static string? OriginalPath(string infoFile)
    {
        try
        {
            var b = File.ReadAllBytes(infoFile);
            long version = BitConverter.ToInt64(b, 0);
            if (version == 2 && b.Length >= 28)
            {
                int chars = BitConverter.ToInt32(b, 24);
                return Encoding.Unicode.GetString(b, 28, Math.Min(chars * 2, b.Length - 28)).TrimEnd('\0');
            }
            if (version == 1 && b.Length >= 24 + 520)
                return Encoding.Unicode.GetString(b, 24, 520).TrimEnd('\0');
        }
        catch { }
        return null;
    }
}
