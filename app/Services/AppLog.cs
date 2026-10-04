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

    private static string _folder = DefaultFolder;

    /// <summary>
    /// Where the log is actually being written, which is <see cref="DefaultFolder"/>
    /// unless something has redirected it.
    /// <para>
    /// A property rather than a fixed value so the test suite can send its output
    /// somewhere harmless. A fair number of tests exist to drive the paths that log
    /// a failure - a settings file that will not parse, an installer that is not
    /// FxSound's - and this log is the one artefact a user is asked to paste into a
    /// bug report. Filling it with failures that never happened on their machine is
    /// worse than not logging at all.
    /// </para>
    /// </summary>
    public static string Folder
    {
        get => _folder;
        private set => _folder = string.IsNullOrWhiteSpace(value) ? DefaultFolder : value;
    }

    /// <summary>
    /// Where the log goes when nothing has overridden it.
    /// <para>
    /// Deliberately the same decision <see cref="ProfileManager.AppDataFolder"/>
    /// makes, and for the same reason: on a portable install the profile sits beside
    /// the executable, so a log left in LocalAppData would be somewhere the user
    /// would not look and the "open log folder" button would open a folder with
    /// nothing in it - on exactly the install where somebody is most likely to be
    /// reading it. Note this uses LocalAppData where the profile uses AppData, so
    /// the two are siblings rather than the same folder.
    /// </para>
    /// </summary>
    internal static string DefaultFolder => Path.Combine(
        ProfileManager.IsPortable && ProfileManager.ExecutableFolder is { } beside
            ? beside
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerTool"),
        "logs");

    public static string Path_ => System.IO.Path.Combine(Folder, "app.log");

    /// <summary>Sends the log somewhere else. Used by the test suite.</summary>
    internal static void RedirectTo(string folder) => Folder = folder;


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

                    // The shift loop. It never ran: with KeptFiles at 2 the loop starts at 0 and the
                    // condition is >= 1, so it had no body. The rotation is correct
                    // without it - the explicit delete of app.1.log above plus the
                    // move of app.log at the end is the whole mechanism at this
                    // setting - but the dead loop sat there looking like the part
                    // that did the work, and would silently do nothing if KeptFiles
                    // were ever raised to 3. Written so that it is true at any
                    // setting: from the oldest kept file down to 1, each moving up
                    // one slot.
                    for (int i = KeptFiles - 1; i >= 1; i--)
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
    public static string Sanitise(string text) => Sanitise(
        text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.UserName);

    /// <summary>
    /// The redaction itself, with this machine's own details passed in.
    /// <para>
    /// Split out so it can be exercised for a user name that is not this one. The
    /// whole point of the change above is what happens to a one or two character
    /// account name, and no machine running the tests is going to have one, so
    /// reading <see cref="Environment.UserName"/> inside would put the behaviour
    /// that matters permanently out of reach of a test.
    /// </para>
    /// <para>
    /// Which is also why there is no length guard on the name. It used to be
    /// skipped for anything under three characters, on the reasoning that a
    /// two-letter string appearing in a message was too likely to be a coincidence
    /// - and that skipped the exact case the second replacement exists for. A
    /// profile on another drive, or kept somewhere unusual, leaves the first
    /// replacement with nothing to match, and then the name survives into a log
    /// that is meant to be pasted into a public bug report.
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
                // Was "(driver withheld it)", which described a refusal that never
                // happened: the live EDID request could not be satisfied on any
                // machine, so nothing was ever withheld. What the flag actually
                // distinguishes is having a cached block against having none, and
                // that is worth saying in a support log.
                + (monitor.EdidWithheld ? "  (read from the Windows cache)" : string.Empty)
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
