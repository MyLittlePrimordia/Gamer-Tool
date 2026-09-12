using System;
using System.Diagnostics;
using System.Security.Principal;

namespace GamerTool.Core;

/// <summary>
/// Whether the CURRENT process is elevated, and a one-click way to relaunch
/// the whole app as administrator instead of the user having to close it,
/// find the exe, and right-click "Run as administrator" themselves.
/// </summary>
public static class ElevationManager
{
    /// <summary>True when this process is already running elevated.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // If we can't even ask, assume "no" - every elevation-gated
            // feature already has its own runas fallback (see NativeEqEngine),
            // so under-reporting elevation just means one extra UAC prompt
            // later rather than a silent failure.
            return false;
        }
    }

    /// <summary>
    /// Relaunches GamerTool.exe elevated (triggers the UAC prompt) and, if
    /// the user approves it, returns true so the caller can shut this
    /// (unelevated) instance down and hand off cleanly. Unlike the
    /// --enable-eq/--disable-eq trampoline in App.xaml.cs, this relaunch
    /// carries NO special args, so the elevated instance falls straight
    /// through to the normal single-instance-mutex + full-UI startup path.
    /// </summary>
    public static bool TryRestartElevated()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return false;

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            });

            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED (1223) - the user clicked "No" on the UAC
            // prompt. Not an error worth surfacing as one; they can just
            // try again.
            return false;
        }
        catch
        {
            return false;
        }
    }
}
