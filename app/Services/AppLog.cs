using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;

namespace GamerTool.Services;

/// <summary>
/// One rolling log for the whole app, and the text the bug button copies.
/// <para>
/// It lives under LocalAppData rather than the roaming profile because a log is
/// machine data, not a setting, and because a roaming profile gets copied to
/// other machines where the paths in it mean nothing.
/// </para>
/// <para>
/// Everything written here is passed through <see cref="Sanitise"/> first. A
/// support log is much more useful when it can be pasted into a chat window, and
/// a log full of C:\Users\somebody is neither shareable nor interesting.
/// </para>
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;

    private const int KeptFiles = 2;

    private static readonly object Gate = new();

    /// <summary>True once something has gone wrong, so the bug button can say so.</summary>
    public static bool SawError { get; private set; }

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamerTool",
        "logs");

    public static string Path_ => System.IO.Path.Combine(Folder, "app.log");


    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message)
    {
        SawError = true;
        Write("WARN ", message);
    }

    public static void Error(string tag, Exception ex)
    {
        SawError = true;

        StringBuilder text = new();
        text.Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(Environment.NewLine);

        if (ex.InnerException is not null)
        {
            text.Append("  caused by ").Append(ex.InnerException.GetType().Name)
                .Append(": ").Append(ex.InnerException.Message).Append(Environment.NewLine);
        }

        text.Append(ex.StackTrace);
        Write("ERROR", tag + " " + text);
    }


    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);

                // Rolling by rotation rather than by trimming, so the newest run
                // is always whole and the previous one is still there beside it.
                if (File.Exists(Path_) && new FileInfo(Path_).Length > MaxBytes)
                {
                    string previous = System.IO.Path.Combine(Folder, "app." + (KeptFiles - 1) + ".log");
                    if (File.Exists(previous))
                    {
                        File.Delete(previous);
                    }

                    for (int i = KeptFiles - 2; i >= 1; i--)
                    {
                        string from = System.IO.Path.Combine(Folder, "app." + i + ".log");
                        string to = System.IO.Path.Combine(Folder, "app." + (i + 1) + ".log");
                        if (File.Exists(from))
                        {
                            File.Move(from, to);
                        }
                    }

                    File.Move(Path_, System.IO.Path.Combine(Folder, "app.1.log"));
                }

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + level + "  " + Sanitise(message);

                // Appended synchronously and the handle closed, so a crash a
                // moment later cannot lose the line that explains it.
                File.AppendAllText(Path_, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // A log that cannot be written must never be the thing that breaks.
        }

        System.Diagnostics.Debug.WriteLine(level + " " + message);
    }


    /// <summary>
    /// Replaces anything that identifies the person using the machine: the user
    /// name in a profile path, and the bare user name wherever it turns up in a
    /// path or a message.
    /// <para>
    /// Done with plain string replacement rather than a pattern. The obvious
    /// version of this used a regular expression, and a user name that is a
    /// single letter or ends in a backslash turns the pattern itself into
    /// something illegal, which is a crash on the very first line of startup.
    /// </para>
    /// </summary>
    public static string Sanitise(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string result = text;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            result = result.Replace(profile, @"C:\Users\<user>", StringComparison.OrdinalIgnoreCase);
        }

        // Catches the shapes the profile replace misses, such as the device path
        // form \\?\C:\Users\name, and a machine whose profile is not under
        // C:\Users at all.
        string name = Environment.UserName;
        if (!string.IsNullOrEmpty(name) && name.Length > 2)
        {
            result = result.Replace(
                @"\Users\" + name,
                @"\Users\<user>",
                StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }


    /// <summary>
    /// The block the bug button puts on the clipboard: what this machine is, what
    /// the app decided about it, and the log itself.
    /// </summary>
    public static string Summary(Func<string>? extra = null)
    {
        StringBuilder text = new();

        text.AppendLine("Gamer Tool diagnostics");
        text.AppendLine("generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        text.AppendLine();

        Assembly self = typeof(AppLog).Assembly;
        text.AppendLine("app       : " + self.GetName().Version);
        text.AppendLine("runtime   : " + Environment.Version);
        text.AppendLine("os        : " + Environment.OSVersion.VersionString + " (" + Environment.OSVersion.Platform + ")");
        text.AppendLine("64-bit    : " + Environment.Is64BitProcess);
        text.AppendLine("dpi       : " + DpiDescription());
        text.AppendLine("log       : " + Sanitise(Path_));

        try
        {
            text.AppendLine("fxsound   : " + (new AudioService().IsInstalled ? "installed" : "not installed")
                + ", running=" + new AudioService().IsRunning);
        }
        catch (Exception)
        {
            text.AppendLine("fxsound   : could not be checked");
        }

        if (extra is not null)
        {
            text.AppendLine();
            text.AppendLine(extra());
        }

        text.AppendLine();
        text.AppendLine("---- log ----");

        try
        {
            text.Append(File.Exists(Path_) ? Sanitise(File.ReadAllText(Path_)) : "(no log yet)");
        }
        catch (Exception ex)
        {
            text.Append("(log could not be read: " + ex.Message + ")");
        }

        return text.ToString();
    }


    private static string DpiDescription()
    {
        try
        {
            // Read from the live window rather than a device context: this is the
            // scale the app is actually laid out at, which is the one that matters
            // when someone is reporting something looks wrong.
            System.Windows.Media.Visual? visual = Application.Current?.MainWindow;
            return visual is null
                ? "unknown"
                : Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(visual).DpiScaleX * 100) + "%";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }


    /// <summary>
    /// One block per display, for Copy diagnostics.
    /// <para>
    /// This is the whole point of the section. Someone with an unfamiliar monitor
    /// should be able to paste one block and have it say what the driver said,
    /// how the display is wired, what the EDID decoded to and how far the probe
    /// got, without anybody having to guess which question to ask next. It
    /// deliberately carries no serial number: this text ends up in public issues.
    /// </para>
    /// </summary>
    public static string BacklightLines(IEnumerable<MonitorProbe> monitors)
    {
        List<string> lines = new();

        foreach (MonitorProbe monitor in monitors)
        {
            lines.Add("  " + monitor.FriendlyOrDevice());
            lines.Add("    stage        : " + BacklightService.StageOf(monitor));
            lines.Add("    capable      : " + monitor.CanControlBacklight);
            lines.Add("    reading      : " + (monitor.Brightness?.ToString() ?? "none"));
            lines.Add("    vcp type     : " + (monitor.Brightness?.CodeType.ToString() ?? "none"));
            lines.Add("    win32        : " + (monitor.LastError ?? "none"));
            lines.Add("    attempts     : " + monitor.Attempts.ToString(CultureInfo.InvariantCulture)
                + "   probe " + monitor.ProbeMs.ToString(CultureInfo.InvariantCulture) + "ms");
            lines.Add("    physical mon : " + (monitor.PhysicalMonitor ?? "none"));
            lines.Add("    link         : " + monitor.Link);
            lines.Add("    edid source  : " + monitor.EdidSource
                + (HardwareBrightness.RegistryMatchBy.Length > 0 && monitor.EdidSource == "registry"
                    ? " (" + Sanitise(HardwareBrightness.RegistryMatchBy) + ")"
                    : string.Empty));
            lines.Add("    edid         : " + monitor.Edid.Summary
                + (monitor.Edid.Verdict == EdidVerdict.Plausible
                    ? string.Empty
                    : " [" + monitor.Edid.Verdict + "]"));

            if (monitor.NoReplyBecause is not null || monitor.BlockedReason is not null)
            {
                lines.Add("    reason       : " + Sanitise(monitor.BlockedReason ?? monitor.NoReplyBecause ?? string.Empty));
            }
        }

        return lines.Count == 0 ? "  (no displays probed)" : string.Join(Environment.NewLine, lines);
    }
}
