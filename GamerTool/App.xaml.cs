using System.Windows;
using GamerTool.Services;

namespace GamerTool;

public partial class App : Application
{
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

        // Capture the original display gamma ramp at startup so we can restore it later
        DisplayService.CaptureOriginalRamp();

        base.OnStartup(e);
    }
}
