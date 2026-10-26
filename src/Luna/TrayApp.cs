using System.Diagnostics;
using Luna.Engines;
using Luna.UI;

namespace Luna;

internal sealed class TrayApp : ApplicationContext
{
    readonly Settings settings = Settings.Load();
    readonly NotifyIcon tray;
    readonly RingMenu ring = new();
    readonly Toast toast = new();
    readonly JobRunner runner = new();
    readonly MouseHook hook;
    readonly SynchronizationContext ui;
    readonly HashSet<int> explorerPids = new();
    readonly List<string> pendingFiles = new();
    readonly System.Windows.Forms.Timer pendingTimer = new() { Interval = 300 };
    readonly System.Windows.Forms.Timer releaseTimer = new() { Interval = 450 };

    public TrayApp(string[] args)
    {
        ui = SynchronizationContext.Current!;
        CleanTemp();
        Integration.Refresh(settings);
        if (!settings.Welcomed && !Integration.StartsWithWindows) Integration.StartsWithWindows = true;

        tray = new NotifyIcon { Icon = AppIcon.Small(), Text = "Luna: Shift-drag a file to convert", Visible = true };
        tray.ContextMenuStrip = BuildMenu();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowHelp(); };

        ring.ActionChosen += Run;
        ring.Warm();

        hook = new MouseHook(() => settings.Enabled && settings.TriggerHeld());
        hook.DragTriggered += OnDragTriggered;
        hook.DragReleased += () => { releaseTimer.Stop(); releaseTimer.Start(); };
        hook.DragMoved += ring.FollowCursor;
        hook.ButtonDown += pt => { if (ring.IsOpen && !ring.InDragMode && !ring.ContainsScreenPoint(pt)) ring.Dismiss(); };
        releaseTimer.Tick += (_, _) =>
        {
            // The drop (if any) arrives after the button-up, so give it a moment before closing.
            releaseTimer.Stop();
            if (ring.InDragMode && (DateTime.UtcNow - ring.LastDrop).TotalMilliseconds > 400) ring.EndDrag();
        };

        runner.Started += job => ui.Post(_ => toast.Track(job, runner.Pending), null);
        runner.Finished += job => ui.Post(_ => toast.Finish(job), null);

        pendingTimer.Tick += (_, _) => FlushPending();
        Ipc.Listen(lines => ui.Post(_ => OnIpc(lines), null));

        if (!settings.Welcomed)
        {
            settings.Welcomed = true;
            settings.Save();
            ShowHelp();
        }
        OnIpc(args);
    }

    // ---------- triggers ----------

    void OnDragTriggered(Point start, Point now)
    {
        if (!settings.Enabled || ring.IsOpen) return;
        if (!Native.KeyDown(Native.VK_LBUTTON)) return;
        if (settings.ExplorerOnly && !StartedInExplorer(start)) return;
        Native.GetCursorPos(out var p);
        ring.ShowForDrag(new Point(p.X, p.Y));
    }

    bool StartedInExplorer(Point pt)
    {
        var hwnd = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT(pt.X, pt.Y)), 2 /* GA_ROOT */);
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (explorerPids.Contains((int)pid)) return true;
        bool isExplorer = string.Equals(Native.ProcessNameOfWindow(hwnd), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        if (isExplorer) explorerPids.Add((int)pid);
        return isExplorer;
    }

    void OnIpc(string[] lines)
    {
        var files = lines.Where(l => l.Length > 0 && !l.StartsWith("--")).ToList();
        if (files.Count == 0)
        {
            if (lines.Contains("--background")) return;
            if (lines.Length > 0 || pendingFiles.Count == 0) ShowHelp(); // launched again by hand
            return;
        }
        pendingFiles.AddRange(files);
        // Explorer starts one process per selected file; collect them into one menu.
        pendingTimer.Stop();
        pendingTimer.Start();
    }

    void FlushPending()
    {
        pendingTimer.Stop();
        if (pendingFiles.Count == 0) return;
        var files = pendingFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        pendingFiles.Clear();
        Native.GetCursorPos(out var p);
        ring.ShowForClick(files, new Point(p.X, p.Y));
    }

    // ---------- running actions ----------

    void Run(Act act, IReadOnlyList<string> files)
    {
        var missing = Tools.Missing(act.Needs);
        if (missing != Need.None)
        {
            OfferInstall(missing, () => Run(act, files));
            return;
        }
        runner.Enqueue(new Job { Act = act, Inputs = files });
    }

    void OfferInstall(Need missing, Action retry)
    {
        var first = Enum.GetValues<Need>().First(n => n != Need.None && missing.HasFlag(n));
        var name = Tools.Describe(first);
        if (Tools.WingetId(first) == null)
        {
            toast.Message($"Needs {name}", first is Need.Word or Need.Excel or Need.PowerPoint
                ? "This conversion uses Microsoft Office, which isn't installed." : $"{name} isn't available on this PC.",
                "\uE7BA", isError: true);
            return;
        }
        toast.Message($"Needs {name}", $"{name} is free and open source. Install it with winget?", "\uE896", seconds: 20,
            button: "Install", action: () => Install(first, retry));
    }

    void Install(Need need, Action retry)
    {
        var name = Tools.Describe(need);
        toast.Message($"Installing {name}…", "Follow the installer window. This can take a minute.", "\uE896", seconds: 600);
        Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("winget",
                    $"install -e --id {Tools.WingetId(need)} --accept-package-agreements --accept-source-agreements") { UseShellExecute = true });
                p!.WaitForExit();
            }
            catch { }
            ui.Post(_ =>
            {
                Tools.Reset();
                bool ok = Tools.Missing(need) == Need.None;
                toast.Message(ok ? $"{name} installed" : $"Couldn't install {name}",
                    ok ? "Running your conversion now." : "Try again from the tray menu > Engines.", ok ? "\uE73E" : "\uE783", !ok, 4);
                if (ok) retry();
            }, null);
        });
    }

    // ---------- tray menu ----------

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        MacMenu.Apply(menu);
        var header = MacMenu.Title("Luna", menu.Font);
        var enabled = new ToolStripMenuItem("Shift-drag menu") { CheckOnClick = true };
        var trigger = new ToolStripMenuItem("Trigger key");
        foreach (var k in Enum.GetValues<TriggerKey>())
        {
            var item = new ToolStripMenuItem(Settings.TriggerName(k)) { Tag = k };
            item.Click += (_, _) => { settings.Trigger = k; settings.Save(); };
            trigger.DropDownItems.Add(item);
        }
        var explorerOnly = new ToolStripMenuItem("Only from File Explorer && Desktop") { CheckOnClick = true };
        var contextMenu = new ToolStripMenuItem("Right-click menu entry") { CheckOnClick = true };
        var startup = new ToolStripMenuItem("Open at login") { CheckOnClick = true };
        var engines = new ToolStripMenuItem("Engines");
        var help = new ToolStripMenuItem("How to use");
        var about = new ToolStripMenuItem("About Luna");
        var uninstall = new ToolStripMenuItem("Uninstall Luna…");
        var quit = new ToolStripMenuItem("Quit Luna");

        enabled.Click += (_, _) => { settings.Enabled = enabled.Checked; settings.Save(); };
        explorerOnly.Click += (_, _) => { settings.ExplorerOnly = explorerOnly.Checked; settings.Save(); };
        contextMenu.Click += (_, _) => { settings.ContextMenu = contextMenu.Checked; settings.Save(); Integration.SetContextMenu(settings.ContextMenu); };
        startup.Click += (_, _) => Integration.StartsWithWindows = startup.Checked;
        help.Click += (_, _) => ShowHelp();
        about.Click += (_, _) => toast.Message($"Luna {Version}", "Drag a file, press Shift, drop it on a format. Free and offline.",
            seconds: 10, button: "GitHub", action: () => OpenUrl(Integration.Homepage));
        uninstall.Click += (_, _) => toast.Message("Uninstall Luna?", "Removes the login item, the right-click entry and settings.",
            "", seconds: 15, button: "Uninstall", action: Uninstall);
        quit.Click += (_, _) => ExitThread();

        menu.Opening += (_, _) =>
        {
            enabled.Checked = settings.Enabled;
            explorerOnly.Checked = settings.ExplorerOnly;
            contextMenu.Checked = settings.ContextMenu;
            startup.Checked = Integration.StartsWithWindows;
            enabled.Text = $"{Settings.TriggerName(settings.Trigger)}-drag menu";
            foreach (ToolStripMenuItem i in trigger.DropDownItems) i.Checked = (TriggerKey)i.Tag! == settings.Trigger;
            Tools.Reset();
            engines.DropDownItems.Clear();
            foreach (var (label, ok, need) in new (string, bool, Need)[]
            {
                ("FFmpeg: images, video, audio", Tools.Ffmpeg != null, Need.Ffmpeg),
                ("Microsoft Office: documents", Tools.Word || Tools.Excel || Tools.PowerPoint, Need.Word),
                ("Microsoft Edge: HTML/Markdown to PDF", Tools.Edge != null, Need.Edge),
                ("qpdf: merge, split, compress PDFs", Tools.Qpdf != null, Need.Qpdf),
                ("Ghostscript: stronger PDF compression", Tools.Ghostscript != null, Need.None),
            })
            {
                // Checked = installed; missing ones are clickable and offer an install.
                var item = new ToolStripMenuItem(ok ? label : label + "  (get it)") { Checked = ok };
                if (!ok && Tools.WingetId(need) != null) item.Click += (_, _) => Install(need, () => { });
                else if (!ok && label.StartsWith("Ghostscript")) item.Click += (_, _) => OpenUrl("https://ghostscript.com/releases/gsdnld.html");
                else if (!ok) item.Enabled = false;
                engines.DropDownItems.Add(item);
            }
        };

        menu.Items.AddRange(new ToolStripItem[]
        {
            header, new ToolStripSeparator(), enabled, trigger, explorerOnly, contextMenu, startup,
            new ToolStripSeparator(), engines, help, about,
            new ToolStripSeparator(), uninstall, quit,
        });
        return menu;
    }

    static string Version => typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "";

    void Uninstall()
    {
        Integration.Uninstall();
        // A running exe can't delete itself; show it so the user can.
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Environment.ProcessPath}\"") { UseShellExecute = false }); } catch { }
        ExitThread();
    }

    void ShowHelp()
    {
        var key = Settings.TriggerName(settings.Trigger);
        toast.Message("Luna is running",
            $"Drag a file, press {key}, and drop it on a format. Hold Alt for tools.",
            "\uE946", seconds: 12, button: "More", action: () => OpenUrl(Integration.Homepage + "#using-luna"));
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    static void CleanTemp()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Luna");
            if (Directory.Exists(root))
                foreach (var d in Directory.GetDirectories(root))
                    if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-6)) Paths.TryDelete(d);
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        hook.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}
