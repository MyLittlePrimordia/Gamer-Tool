using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace GamerTool.Core;

/// <summary>
/// Toggles "Run on Windows Startup" via the per-user HKCU Run key. Deliberately
/// uses HKCU rather than HKLM so no elevation is ever required - consistent
/// with the app's asInvoker execution level (gamma ramps and the Run key both
/// operate at standard user rights).
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GamerTool";

    /// <summary>Reads current state directly from the registry rather than a cached flag, so external edits (e.g. a user manually removing the entry) are always reflected.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables startup, pointing the Run entry at the current executable with a
    /// "--minimized" launch flag so a startup launch goes straight to the tray
    /// instead of popping the main window in front of whatever the user is
    /// already doing at login.
    /// </summary>
    public static bool Enable()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return false;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            key?.SetValue(ValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Convenience wrapper for the "Run on Windows Startup" toggle switch in Settings.</summary>
    public static bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();
}
