using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Luna.Engines;

/// <summary>
/// Late-bound Office automation for one job. Instances we start are quit (or killed on cancel);
/// an Office app the user already had open is never closed.
/// </summary>
internal sealed class OfficeSession : IDisposable
{
    sealed class App(object com, bool owned, int[] pids)
    {
        public object Com = com;
        public bool Owned = owned;
        public int[] Pids = pids;
    }

    readonly Dictionary<string, App> apps = new();
    readonly CancellationTokenRegistration reg;

    public OfficeSession(CancellationToken ct) => reg = ct.Register(KillOwned);

    public dynamic Word => Get("Word.Application", "WINWORD", app =>
    {
        app.Visible = false;
        app.DisplayAlerts = 0;          // wdAlertsNone
        app.AutomationSecurity = 3;     // msoAutomationSecurityForceDisable: never run macros
        app.Options.SavePropertiesPrompt = false;
    });

    public dynamic Excel => Get("Excel.Application", "EXCEL", app =>
    {
        app.Visible = false;
        app.DisplayAlerts = false;
        app.AskToUpdateLinks = false;
        app.AutomationSecurity = 3;
    });

    public dynamic PowerPoint => Get("PowerPoint.Application", "POWERPNT", app =>
    {
        app.DisplayAlerts = 1;          // ppAlertsNone
        app.AutomationSecurity = 3;
    });

    dynamic Get(string progId, string processName, Action<dynamic> init)
    {
        if (apps.TryGetValue(progId, out var existing)) return existing.Com;
        var before = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        var type = Type.GetTypeFromProgID(progId) ?? throw new ToolException($"{progId.Split('.')[0]} is not installed.");
        object com = Activator.CreateInstance(type)!;
        var spawned = Process.GetProcessesByName(processName).Select(p => p.Id).Where(id => !before.Contains(id)).ToArray();
        var app = new App(com, owned: spawned.Length > 0, spawned);
        apps[progId] = app;
        try { init(com); } catch (COMException) { }
        return com;
    }

    void KillOwned()
    {
        foreach (var app in apps.Values.Where(a => a.Owned))
            foreach (var pid in app.Pids)
                try { Process.GetProcessById(pid).Kill(); } catch { }
    }

    public void Dispose()
    {
        reg.Dispose();
        foreach (var (progId, app) in apps)
        {
            try
            {
                if (app.Owned)
                {
                    dynamic com = app.Com;
                    if (progId.StartsWith("Word")) com.Quit(0); else com.Quit();
                }
            }
            catch { }
            try { Marshal.FinalReleaseComObject(app.Com); } catch { }
        }
        apps.Clear();
    }

    /// <summary>
    /// Opening a PDF in Word shows a modal "Word will now convert your PDF" prompt that automation
    /// can't dismiss. This sets the same flag as that prompt's "Don't show this message again" box.
    /// </summary>
    public static void AllowSilentPdfOpen()
    {
        try
        {
            using var office = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
            if (office == null) return;
            foreach (var version in office.GetSubKeyNames().Where(n => n.Contains('.') && char.IsDigit(n[0])))
            {
                using var word = office.OpenSubKey($@"{version}\Word");
                if (word == null) continue;
                using var options = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
                if (options.GetValue("DisableConvertPdfWarning") is not 1)
                    options.SetValue("DisableConvertPdfWarning", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
            try { Marshal.FinalReleaseComObject(com); } catch { }
    }
}
