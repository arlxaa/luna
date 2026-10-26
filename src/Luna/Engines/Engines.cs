using System.Diagnostics;

namespace Luna.Engines;

[Flags]
internal enum Need
{
    None = 0, Ffmpeg = 1, Word = 2, PowerPoint = 4, Excel = 8, Edge = 16, Qpdf = 32, Tar = 64,
}

/// <summary>Finds the external tools Luna drives. Nothing is bundled; everything is optional.</summary>
internal static class Tools
{
    static readonly Dictionary<string, string?> cache = new();
    static readonly Dictionary<string, bool> progIds = new();

    public static string? Ffmpeg => Find("ffmpeg");
    public static string? Ffprobe => Ffmpeg is { } f ? Sibling(f, "ffprobe.exe") ?? Find("ffprobe") : null;
    public static string? Qpdf => Find("qpdf", () => Glob(@"%ProgramFiles%\qpdf*\bin\qpdf.exe"));
    public static string? Ghostscript => Find("gswin64c", () => Glob(@"%ProgramFiles%\gs\gs*\bin\gswin64c.exe"));
    public static string? Tar => File.Exists(Path.Combine(Environment.SystemDirectory, "tar.exe")) ? Path.Combine(Environment.SystemDirectory, "tar.exe") : null;
    public static string? Edge => new[]
    {
        Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
        Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
    }.FirstOrDefault(File.Exists);

    public static bool Word => HasProgId("Word.Application");
    public static bool Excel => HasProgId("Excel.Application");
    public static bool PowerPoint => HasProgId("PowerPoint.Application");

    public static bool Has(Need need) => Missing(need) == Need.None;

    public static Need Missing(Need need)
    {
        Need missing = Need.None;
        if (need.HasFlag(Need.Ffmpeg) && Ffmpeg is null) missing |= Need.Ffmpeg;
        if (need.HasFlag(Need.Word) && !Word) missing |= Need.Word;
        if (need.HasFlag(Need.Excel) && !Excel) missing |= Need.Excel;
        if (need.HasFlag(Need.PowerPoint) && !PowerPoint) missing |= Need.PowerPoint;
        if (need.HasFlag(Need.Edge) && Edge is null) missing |= Need.Edge;
        if (need.HasFlag(Need.Qpdf) && Qpdf is null) missing |= Need.Qpdf;
        if (need.HasFlag(Need.Tar) && Tar is null) missing |= Need.Tar;
        return missing;
    }

    public static string Describe(Need n) => n switch
    {
        Need.Ffmpeg => "FFmpeg",
        Need.Word => "Microsoft Word",
        Need.Excel => "Microsoft Excel",
        Need.PowerPoint => "Microsoft PowerPoint",
        Need.Edge => "Microsoft Edge",
        Need.Qpdf => "qpdf",
        Need.Tar => "Windows tar",
        _ => n.ToString(),
    };

    /// <summary>winget id for engines that can be installed on demand.</summary>
    public static string? WingetId(Need n) => n switch
    {
        Need.Ffmpeg => "Gyan.FFmpeg",
        Need.Qpdf => "QPDF.QPDF",
        _ => null,
    };

    public static void Reset()
    {
        lock (cache) cache.Clear();
        lock (progIds) progIds.Clear();
    }

    static bool HasProgId(string id)
    {
        lock (progIds)
        {
            if (!progIds.TryGetValue(id, out bool ok))
                progIds[id] = ok = Type.GetTypeFromProgID(id) != null;
            return ok;
        }
    }

    static string? Find(string name, Func<string?>? fallback = null)
    {
        lock (cache)
        {
            if (cache.TryGetValue(name, out var hit)) return hit;
            string? found = SearchPath(name + ".exe") ?? fallback?.Invoke();
            cache[name] = found;
            return found;
        }
    }

    static string? SearchPath(string exe)
    {
        // Read PATH fresh from the registry so tools installed after login are found without a restart.
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Links"),
        };
        foreach (var dir in paths.Where(p => p != null).SelectMany(p => p!.Split(';')))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    static string? Sibling(string file, string name)
    {
        var p = Path.Combine(Path.GetDirectoryName(file)!, name);
        return File.Exists(p) ? p : null;
    }

    static string? Glob(string pattern)
    {
        pattern = Environment.ExpandEnvironmentVariables(pattern);
        // Pattern shape: <root>\<dirGlob>\...\<file>; only one wildcard directory level is supported.
        var parts = pattern.Split('\\');
        int star = Array.FindIndex(parts, p => p.Contains('*'));
        if (star < 0) return File.Exists(pattern) ? pattern : null;
        var root = string.Join('\\', parts[..star]);
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.GetDirectories(root, parts[star]).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts[(star + 1)..]).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

internal sealed class ToolException(string message) : Exception(message);

/// <summary>Runs command-line tools hidden, with cancellation and captured error output.</summary>
internal static class Proc
{
    public static string Run(string exe, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onStdout = null, int timeoutSeconds = 0, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new ToolException($"Could not start {Path.GetFileName(exe)}");
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onStdout?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var started = DateTime.UtcNow;
        while (!p.WaitForExit(100))
        {
            bool timedOut = timeoutSeconds > 0 && (DateTime.UtcNow - started).TotalSeconds > timeoutSeconds;
            if (ct.IsCancellationRequested || timedOut)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(2000);
                ct.ThrowIfCancellationRequested();
                throw new ToolException($"{Path.GetFileName(exe)} took too long and was stopped.");
            }
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new ToolException(Summarize(stderr.ToString(), stdout.ToString(), Path.GetFileNameWithoutExtension(exe)));
        return stdout.ToString();
    }

    static string Summarize(string stderr, string stdout, string tool)
    {
        var lines = (stderr + "\n" + stdout).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("frame=") && !l.Contains("progress=")).ToArray();
        var tail = lines.Length == 0 ? "unknown error" : string.Join(" ", lines.TakeLast(2));
        if (tail.Length > 300) tail = tail[^300..];
        return $"{tool} failed: {tail}";
    }
}
