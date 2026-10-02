using System;
using System.IO;
using Microsoft.Win32;

namespace GamerTool.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "GamerTool";

    public string ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return Path.Combine(AppContext.BaseDirectory, "GamerTool.exe");
        }
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);

                // Matches the executable this run is actually made of, not the
                // string anywhere in the value. It is written quoted with the path
                // and " --tray", so the leaf can be pulled out and compared - and a
                // plain Contains matched any value with "GamerTool" anywhere in it,
                // so a stale entry pointing at C:\Tools\NotGamerTool\x.exe read as
                // this app being set to start. The user then saw the switch on and
                // nothing happened at boot.
                if (key?.GetValue(ValueName) is not string value)
                {
                    return false;
                }

                string trimmed = value.Trim().Trim('"');
                int space = trimmed.IndexOf(' ');
                string path = space > 0 ? trimmed[..space] : trimmed;

                return string.Equals(
                    Path.GetFileName(path),
                    Path.GetFileName(ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                TraceLog.Write("STARTUP", ex);
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string path = ExecutablePath;
                if (path.Length == 0 || !File.Exists(path))
                {
                    return false;
                }

                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                key.SetValue(ValueName, "\"" + path + "\" --tray", RegistryValueKind.String);
                return true;
            }

            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (runKey is not null)
            {
                runKey.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            TraceLog.Write("STARTUP", ex);
            return false;
        }
    }
}
