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

                string path = ReadConfiguredPath(value);
                if (path.Length == 0)
                {
                    return false;
                }

                string mine;
                string theirs;
                try
                {
                    mine = Path.GetFileName(ExecutablePath);
                    theirs = Path.GetFileName(path);
                }
                catch (ArgumentException)
                {
                    // A hand-edited or truncated value can hold a character path
                    // rejects. That is not this app's entry, whatever it says.
                    return false;
                }

                return string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                TraceLog.Write("STARTUP", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Pulls the executable path back out of the value this class writes.
    /// <para>
    /// The value is written as a quoted path followed by " --tray", and it used to be
    /// recovered with <c>Trim().Trim('"')</c> and a split on the first space. Both
    /// halves of that were wrong against the app's own output, and neither could be
    /// seen without reading the two together: the trailing quote is not at the end of
    /// the string, because the string ends in the y of "--tray", so trimming quotes
    /// only ever removed the leading one and left <c>GamerTool.exe"</c> behind; and
    /// the first space is inside the path whenever any directory has one in it, which
    /// for this app's own install folder is the normal case rather than the rare one.
    /// Between them the reader could not return true for any value the writer had
    /// produced, so the switch always read off while the app autostarted.
    /// </para>
    /// <para>
    /// Reading the quoted section instead fixes both at once, because that is the
    /// format the value is actually written in. The unquoted branch is for an entry
    /// some other tool wrote, where the path is whatever precedes the arguments.
    /// </para>
    /// </summary>
    internal static string ReadConfiguredPath(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.Length > 0 && trimmed[0] == '"')
        {
            int close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : trimmed.Trim('"');
        }

        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
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
