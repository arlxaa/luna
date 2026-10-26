using System.Runtime.InteropServices;

namespace Luna;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Luna.exe                → start (or say hello if already running)
    /// Luna.exe --background   → autostart at login, silent
    /// Luna.exe --menu "file"  → Explorer right-click verb: show the menu for that file
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        using var mutex = new Mutex(true, Ipc.MutexName, out bool first);
        if (!first)
        {
            // We were launched by Explorer and may take the foreground; pass that right on.
            AllowSetForegroundWindow(-1);
            Ipc.Send(args.Length == 0 ? new[] { "--show" } : args);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(args));
        GC.KeepAlive(mutex);
        return 0;
    }
}

internal static class AppIcon
{
    static Stream? Resource() => typeof(AppIcon).Assembly.GetManifestResourceStream("luna.ico");

    public static Icon Small()
    {
        using var s = Resource();
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    /// <summary>
    /// The icon as a bitmap of <paramref name="size"/> px, decoded from the smallest embedded PNG
    /// frame at least that large (Icon.ToBitmap would pick a tiny frame and upscale it).
    /// </summary>
    public static Bitmap Bitmap(int size)
    {
        using var s = Resource();
        if (s == null) return SystemIcons.Application.ToBitmap();
        using var r = new BinaryReader(s);
        r.ReadUInt16(); r.ReadUInt16();
        int count = r.ReadUInt16();
        var frames = Enumerable.Range(0, count).Select(_ =>
        {
            int w = r.ReadByte(); r.ReadBytes(7);
            int length = r.ReadInt32(), offset = r.ReadInt32();
            return (size: w == 0 ? 256 : w, length, offset);
        }).ToList();
        var pick = frames.Where(f => f.size >= size).OrderBy(f => f.size).DefaultIfEmpty(frames.MaxBy(f => f.size)).First();
        s.Position = pick.offset;
        using var png = new MemoryStream(r.ReadBytes(pick.length));
        using var frame = new Bitmap(png);
        var result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(frame, 0, 0, size, size);
        return result;
    }
}
