using System.Runtime.InteropServices;

namespace Luna;

/// <summary>
/// Low-level mouse hook that notices a left-button drag with the trigger key held. It runs on its
/// own thread so UI work never delays the system cursor, and only posts events to the UI thread.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Native.POINT pt; public uint lPrivate; }

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    const uint WM_QUIT = 0x0012;

    readonly Native.LowLevelMouseProc proc; // keep the delegate alive while hooked
    readonly SynchronizationContext ui;
    readonly Func<bool> triggerHeld;
    readonly Thread thread;
    uint threadId;
    IntPtr hook;

    // Hook-thread state.
    bool down, fired;
    Point downAt;
    long lastMove;  // packed x<<32 | y, shared with the UI thread
    int movePosted; // at most one DragMoved post in flight

    /// <summary>Drag past the threshold with the trigger key held: (where the drag started, cursor now).</summary>
    public event Action<Point, Point>? DragTriggered;
    public event Action? DragReleased;
    /// <summary>Cursor moves after a trigger, until release.</summary>
    public event Action<Point>? DragMoved;
    /// <summary>Any button pressed anywhere (used to dismiss the click-mode menu).</summary>
    public event Action<Point>? ButtonDown;

    public MouseHook(Func<bool> triggerHeld)
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create the hook on the UI thread.");
        this.triggerHeld = triggerHeld;
        proc = Callback;
        using var ready = new ManualResetEventSlim();
        int error = 0;
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            error = hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            ready.Set();
            if (hook == IntPtr.Zero) return;
            // Hook callbacks are delivered while this thread waits in GetMessage.
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            Native.UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "mouse-hook", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait();
        if (error != 0) throw new InvalidOperationException($"Mouse hook failed ({error}).");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var pt = new Point(info.pt.X, info.pt.Y);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    down = true;
                    fired = false;
                    downAt = pt;
                    ui.Post(_ => ButtonDown?.Invoke(pt), null);
                    break;
                case Native.WM_RBUTTONDOWN:
                case Native.WM_MBUTTONDOWN:
                    ui.Post(_ => ButtonDown?.Invoke(pt), null);
                    break;
                case Native.WM_MOUSEMOVE when down && fired:
                    Interlocked.Exchange(ref lastMove, ((long)pt.X << 32) | (uint)pt.Y);
                    if (Interlocked.Exchange(ref movePosted, 1) == 0)
                        ui.Post(_ =>
                        {
                            Interlocked.Exchange(ref movePosted, 0);
                            long packed = Interlocked.Read(ref lastMove);
                            DragMoved?.Invoke(new Point((int)(packed >> 32), (int)packed));
                        }, null);
                    break;
                case Native.WM_MOUSEMOVE when down:
                    int dx = pt.X - downAt.X, dy = pt.Y - downAt.Y;
                    int threshold = Math.Max(8, Native.GetSystemMetrics(Native.SM_CXDRAG) * 2);
                    if (dx * dx + dy * dy >= threshold * threshold && triggerHeld())
                    {
                        fired = true;
                        var start = downAt;
                        ui.Post(_ => DragTriggered?.Invoke(start, pt), null);
                    }
                    break;
                case Native.WM_LBUTTONUP:
                    down = false;
                    if (fired)
                    {
                        fired = false;
                        ui.Post(_ => DragReleased?.Invoke(), null);
                    }
                    break;
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        threadId = 0;
    }
}
