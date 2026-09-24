using System.Windows;
using GamerTool.Services;

namespace GamerTool;

public partial class App : Application
{
    public const string ElevatedCableSetupArg = "--elevated-cable-setup";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Handle re-launches with an elevated helper flag: these runs do their
        // one job and exit immediately, WITHOUT showing any WPF window. This
        // keeps the "needs admin" surface area to a single, short-lived,
        // invisible relaunch instead of making the whole app run elevated.
        if (e.Args.Contains(AudioService.ElevatedSetupArg))
        {
            int code = AudioService.RunElevatedSetupEntryPoint();
            Shutdown(code);
            return;
        }

        if (e.Args.Contains(AudioService.ElevatedPanicArg))
        {
            int code = AudioService.RunElevatedPanicResetEntryPoint();
            Shutdown(code);
            return;
        }

        if (e.Args.Contains(ElevatedCableSetupArg))
        {
            string logPath = System.IO.Path.Combine(
                Environment.ExpandEnvironmentVariables(@"%ProgramData%\GamerTool"), "cable-setup.log");
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!); } catch { }
            var outcome = VirtualCableInstallerService.RunFullSetup(logPath);
            Shutdown((int)outcome);
            return;
        }

        base.OnStartup(e);
    }
}
