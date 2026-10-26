using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Luna;

internal enum TriggerKey { Shift, Alt, CtrlShift }

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public TriggerKey Trigger { get; set; } = TriggerKey.Shift;
    public bool ExplorerOnly { get; set; } = true;
    public bool ContextMenu { get; set; } = true;
    public bool Welcomed { get; set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Luna");
    static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public bool TriggerHeld() => Trigger switch
    {
        TriggerKey.Alt => Native.KeyDown(Native.VK_MENU),
        TriggerKey.CtrlShift => Native.KeyDown(Native.VK_CONTROL) && Native.KeyDown(Native.VK_SHIFT),
        _ => Native.KeyDown(Native.VK_SHIFT),
    };

    public static string TriggerName(TriggerKey k) => k switch
    {
        TriggerKey.Alt => "Alt",
        TriggerKey.CtrlShift => "Ctrl+Shift",
        _ => "Shift",
    };
}

/// <summary>Per-user registration: autostart and the Explorer right-click verb (no admin needed).</summary>
internal static class Integration
{
    public const string Homepage = "https://github.com/arlxaa/luna";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string[] VerbKeys = { @"Software\Classes\*\shell\Luna", @"Software\Classes\Directory\shell\Luna" };

    static string Exe => Environment.ProcessPath!;

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Luna") is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Luna", $"\"{Exe}\" --background");
            else k.DeleteValue("Luna", false);
        }
    }

    public static void SetContextMenu(bool on)
    {
        foreach (var path in VerbKeys)
        {
            if (!on) { Registry.CurrentUser.DeleteSubKeyTree(path, false); continue; }
            using var k = Registry.CurrentUser.CreateSubKey(path);
            k.SetValue("", "Convert with Luna");
            k.SetValue("Icon", $"\"{Exe}\",0");
            k.SetValue("MultiSelectModel", "Player");
            using var c = k.CreateSubKey("command");
            c.SetValue("", $"\"{Exe}\" --menu \"%1\"");
        }
    }

    /// <summary>Re-point existing registrations if the exe moved.</summary>
    public static void Refresh(Settings s)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            if (k?.GetValue("Luna") is string run && !run.Contains(Exe, StringComparison.OrdinalIgnoreCase))
                StartsWithWindows = true;
        SetContextMenu(s.ContextMenu);
    }

    /// <summary>Removes everything Luna wrote to the registry and its settings folder.</summary>
    public static void Uninstall()
    {
        StartsWithWindows = false;
        SetContextMenu(false);
        try { Directory.Delete(Settings.Dir, true); } catch { }
    }
}
