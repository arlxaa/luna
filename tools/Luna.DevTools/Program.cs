using Luna.DevTools;

// dotnet run --project tools/Luna.DevTools -- <command> <dir>
// Commands run on an STA thread because Office automation (smoke) and WinForms require it.
int exit = 2;
var worker = new Thread(() => exit = args.FirstOrDefault() switch
{
    "icon" when args.Length == 2 => IconCommand.Run(args[1]),
    "screenshots" when args.Length == 2 => ScreenshotsCommand.Run(args[1]),
    "smoke" when args.Length == 2 => SmokeCommand.Run(args[1]),
    _ => Usage(),
});
worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();
return exit;

static int Usage()
{
    Console.WriteLine("""
        Luna developer tools

          icon <dir>          Generate luna.ico (+ luna-256.png) into <dir>
          screenshots <dir>   Render README screenshots into <dir>
          smoke <dir>         Create sample files in <dir> and run every menu action

        Example:
          dotnet run --project tools/Luna.DevTools -- smoke %TEMP%\luna-smoke
        """);
    return 2;
}
