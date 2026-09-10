using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GamerTool.Core;

/// <summary>
/// Polls which process currently owns the foreground window and reports it
/// as a simple executable name (e.g. "cod.exe"), so the ViewModel can match
/// it against combos with AutoActivateOnLaunch set. Deliberately simple -
/// no window hooks, no COM, just GetForegroundWindow + GetWindowThreadProcessId
/// on a timer, the same technique overlay/macro tools have used for twenty
/// years, and it needs no elevation.
/// </summary>
public static class GameLaunchWatcher
{
    /// <summary>
    /// The executable name (lowercase, with extension, e.g. "javaw.exe") of
    /// whatever process currently owns the foreground window, or null if it
    /// can't be determined (permission-restricted process, no window, etc.).
    /// </summary>
    public static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = User32Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            User32Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return null;

            using var process = Process.GetProcessById((int)pid);
            return (process.ProcessName + ".exe").ToLowerInvariant();
        }
        catch
        {
            // Elevated/system processes can throw Win32Exception on access -
            // "unknown" is the safe answer, never a crash.
            return null;
        }
    }

    /// <summary>
    /// A snapshot of currently running, user-visible processes (has a main
    /// window with a title) for the Combos "launch with" picker - excludes
    /// background services and GamerTool itself.
    /// </summary>
    public static string[] GetRunningAppProcessNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    if (string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        continue;

                    var exe = process.ProcessName + ".exe";
                    if (string.Equals(exe, "GamerTool.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    names.Add(exe);
                }
                catch
                {
                    // Access-denied on a single process shouldn't blank the whole list.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Leave whatever was collected so far.
        }

        var result = new string[names.Count];
        names.CopyTo(result);
        return result;
    }
}
