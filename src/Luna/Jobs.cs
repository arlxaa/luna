using System.Collections.Concurrent;
using Luna.Engines;

namespace Luna;

internal enum JobState { Queued, Running, Done, Failed, Canceled }

internal sealed class Job
{
    public required Act Act { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public CancellationTokenSource Cts { get; } = new();
    public CancellationToken Token => Cts.Token;

    public List<string> Outputs { get; } = new();
    public List<string> Errors { get; } = new();
    /// <summary>Text copied to the clipboard (QR codes, OCR) instead of a file.</summary>
    public string? ResultText { get; set; }
    public string? Note { get; set; }
    /// <summary>Reverses the job (puts deleted items back); returns how many items were restored.</summary>
    public Func<int>? Undo { get; set; }

    public JobState State { get; set; }
    public int Index { get; set; }
    public string Current { get; set; } = "";
    /// <summary>0..1 for the current file, or -1 when unknown.</summary>
    public double Progress { get; private set; } = -1;

    OfficeSession? office;
    public OfficeSession Office => office ??= new OfficeSession(Token);

    public void Report(double progress)
    {
        Progress = Math.Clamp(progress, 0, 1);
    }

    internal void EndOffice()
    {
        office?.Dispose();
        office = null;
    }
}

/// <summary>Runs jobs one at a time on a dedicated STA thread (Office automation needs STA).</summary>
internal sealed class JobRunner
{
    readonly BlockingCollection<Job> queue = new();
    public event Action<Job>? Started, Finished;
    public int Pending => queue.Count;

    public JobRunner()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "jobs" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public void Enqueue(Job job) => queue.Add(job);

    void Loop()
    {
        foreach (var job in queue.GetConsumingEnumerable())
        {
            job.State = JobState.Running;
            Started?.Invoke(job);
            try
            {
                if (job.Act.Batch != null)
                {
                    job.Current = job.Inputs[0];
                    job.Act.Batch(job, job.Inputs);
                }
                else
                {
                    for (int i = 0; i < job.Inputs.Count; i++)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        job.Index = i;
                        job.Current = job.Inputs[i];
                        job.Report(0);
                        try { job.Act.PerFile!(job, job.Inputs[i]); }
                        catch (Exception ex) when (ex is not OperationCanceledException && job.Inputs.Count > 1)
                        {
                            job.Errors.Add($"{Path.GetFileName(job.Inputs[i])}: {Friendly(ex)}");
                        }
                    }
                }
                job.State = job.Errors.Count > 0 && job.Outputs.Count == 0 && job.ResultText == null ? JobState.Failed : JobState.Done;
            }
            catch (OperationCanceledException) { job.State = JobState.Canceled; }
            catch (Exception ex)
            {
                job.Errors.Add(Friendly(ex));
                job.State = JobState.Failed;
            }
            finally
            {
                try { job.EndOffice(); } catch { }
            }
            Finished?.Invoke(job);
        }
    }

    public static string Friendly(Exception ex) => ex switch
    {
        ToolException or InvalidOperationException => ex.Message,
        System.Runtime.InteropServices.COMException com => $"Office reported an error ({com.Message.Trim()})",
        UnauthorizedAccessException => "Access denied. Is the folder read-only?",
        IOException io => io.Message,
        _ => ex.Message,
    };
}

internal static class Paths
{
    static readonly string[] DoubleExts = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" };

    /// <summary>File name without extension; strips compound archive extensions like .tar.gz.</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Directory.Exists(path)) return name;
        foreach (var de in DoubleExts)
            if (name.EndsWith(de, StringComparison.OrdinalIgnoreCase)) return name[..^de.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>"photo.heic" + "png" → "photo.png" next to the original, never overwriting anything.</summary>
    public static string Out(string input, string ext, string? tag = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input.TrimEnd('\\', '/')))!;
        var name = BaseName(input) + (tag is null ? "" : $" ({tag})");
        return Unique(Path.Combine(dir, ext.Length == 0 ? name : $"{name}.{ext}"));
    }

    public static string OutDir(string input, string? tag = null) => Out(input, "", tag);

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var file = Path.GetFileName(path);
        string name = file, ext = "";
        foreach (var de in DoubleExts.Append(Path.GetExtension(file)))
            if (de.Length > 0 && file.EndsWith(de, StringComparison.OrdinalIgnoreCase)) { name = file[..^de.Length]; ext = de; break; }
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Luna", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static int Logical(string a, string b) => Native.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));
}
