using System.IO.Pipes;
using System.Text;

namespace Luna;

/// <summary>
/// Single-instance plumbing. Explorer launches one process per selected file for a context-menu
/// verb, so every secondary instance forwards its arguments here and exits.
/// </summary>
internal static class Ipc
{
    static readonly string Id = $"Luna-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    public static string MutexName => "Local\\" + Id;
    static string PipeName => Id;

    public static void Send(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false));
            foreach (var a in args) w.WriteLine(a);
        }
        catch { }
    }

    public static void Listen(Action<string[]> onMessage)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe, Encoding.UTF8);
                    var lines = new List<string>();
                    while (r.ReadLine() is { } line) lines.Add(line);
                    onMessage(lines.ToArray());
                }
                catch { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "ipc" };
        t.Start();
    }
}
