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

    /// <summary>
    /// Where the log is written.
    /// <para>
    /// Follows the profile, so a portable install is genuinely portable: the
    /// settings file, the preset files it points at and the log all travel
    /// together on one folder, and nothing is left behind in the user profile
    /// pointing at a drive that is no longer there.
    /// </para>
    /// <para>
    /// A computed property rather than a field, because whether the install is
    /// portable is decided by whether a settings file is sitting next to the
    /// executable, and that can change while the app is running. The Settings tab
    /// reads this when its "open log folder" button is pressed, so it has to be
    /// the current answer rather than the one from startup.
    /// </para>
    /// </summary>
    /// <summary>
    /// Set by the test suite so it stops writing into the real log.
    /// <para>
    /// Tests deliberately exercise paths that log errors - a damaged settings
    /// file, an unreadable one, engine output that is not JSON - and this class
    /// writes to one fixed folder. Without somewhere else to put them, every test
    /// run fills the log a user would paste into a bug report with failures that
    /// never happened on their machine.
    /// </para>
    /// </summary>
    private static string? _folderOverride;

    /// <summary>Points the log somewhere else. For tests.</summary>
    internal static void RedirectTo(string folder) => _folderOverride = folder;

    /// <summary>
    /// Where the log would go, ignoring the test redirect. The rule itself, with
    /// nothing layered over it.
    /// </summary>
    internal static string DefaultFolder => Path.Combine(
        ProfileManager.IsPortable && ProfileManager.ExecutableFolder is { } beside
            ? beside
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerTool"),
        "logs");

    public static string Folder => _folderOverride ?? DefaultFolder;

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
        Write("ERROR", tag + " " + Describe(ex));
    }


    /// <summary>
    /// An exception written out with its whole cause chain, not just the top.
    /// <para>
    /// This walks every level, one indented line each, and the deepest exception is
    /// usually the only one worth reading. It used to unwrap exactly one level, and
    /// that was not enough to diagnose a real failure: WPF wraps a template it
    /// cannot parse three deep, so the log line said
    /// "Provide value on 'System.Windows.StaticResourceExtension' threw an
    /// exception", which names a resource and blames a lookup, while the actual
    /// cause two levels down was "Must have non-null value for 'Binding'". Reading
    /// the top of that chain sends you looking for a missing resource that was
    /// never missing. The stack trace of every level is kept for the same reason,
    /// since the frame that threw is usually not the frame that decided.
    /// </para>
    /// </summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        StringBuilder text = new();
        int depth = 0;

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (depth > 0)
            {
                text.Append("  caused by ");
            }

            text.Append(new string(' ', depth * 2))
                .Append(current.GetType().Name)
                .Append(": ")
                .Append(current.Message)
                .Append(Environment.NewLine);

            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                text.Append(new string(' ', depth * 2 + 2))
                    .Append(current.StackTrace)
                    .Append(Environment.NewLine);
            }

            depth++;

            // A cycle here would hang the log writer, and a malformed exception
            // graph is exactly the sort of thing that turns up in a crash handler.
            if (depth > 16)
            {
                text.Append("  ... chain truncated").Append(Environment.NewLine);
                break;
            }
        }

        return text.ToString();
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
    /// Replaces anything that identifies the person using the machine: the profile
    /// folder, and the user name wherever it appears as a path segment.
    /// <para>
    /// Plain string replacement rather than a pattern, and the two behave very
    /// differently here. A pattern has to be built out of the name, so a name that
    /// is a single letter or ends in a backslash makes the pattern itself illegal -
    /// a crash on the first line of startup, on the path every log write goes
    /// through. Plain replacement has no such problem with any name at all.
    /// </para>
    /// <para>
    /// This used to guard the second replacement with <c>name.Length &gt; 2</c>,
    /// left over from the pattern days, where skipping the awkward names was the
    /// whole fix. With plain replacement the guard no longer prevents anything and
    /// only achieves one thing: an account called "ab" or "x" had its name left in
    /// the log verbatim, in exactly the shapes the profile replace cannot catch -
    /// the <c>\\?\</c> device path form, and a profile that is not under
    /// C:\Users at all. The log is what a user pastes into a public bug report, so
    /// a name that survives it is a disclosure, and the reason for the guard is
    /// gone.
    /// </para>
    /// </summary>
    public static string Sanitise(string text) => Sanitise(
        text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.UserName);

    /// <summary>
    /// The redaction itself, with the machine's own details passed in.
    /// <para>
    /// Split out so it can be exercised for a user name that is not this one. The
    /// whole point of the change above is what happens to a one or two character
    /// account name, and no machine running the tests is going to have one, so
    /// reading <see cref="Environment.UserName"/> inside would put the behaviour
    /// that matters permanently out of reach of a test.
    /// </para>
    /// </summary>
    internal static string Sanitise(string text, string profile, string name)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string result = text;

        if (!string.IsNullOrEmpty(profile))
        {
            result = result.Replace(profile, @"C:\Users\<user>", StringComparison.OrdinalIgnoreCase);
        }

        // Catches the shapes the profile replace misses, such as the device path
        // form \\?\C:\Users\name, and a machine whose profile is not under
        // C:\Users at all.
        if (!string.IsNullOrEmpty(name))
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
            lines.Add("    target       : " + monitor.Target.Summary);
            lines.Add("    amd hdcp     : " + monitor.Protection.Summary
                + (monitor.Protection.AdapterKey.Length > 0
                    ? "  [class " + monitor.Protection.AdapterKey + "]"
                    : string.Empty));
            lines.Add("    edid source  : " + monitor.EdidSource
                + (monitor.EdidWithheld ? "  (driver withheld it)" : string.Empty)
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
