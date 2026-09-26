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
                return key?.GetValue(ValueName) is string value && value.Contains("GamerTool", StringComparison.OrdinalIgnoreCase);
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
