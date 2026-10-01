using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GamerTool.Services;
using Application = System.Windows.Application;

namespace GamerTool;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The developer entry points are compiled out of a release build. They
        // widen what a shipped binary will do on a command line: --makeicon
        // writes a file to a path the caller names, and --selftest walks the
        // drive enumerating installed programs and writes to the production log.
        // Neither is a vulnerability on its own, and both are genuinely useful
        // while working on the icon and on game scanning, so they are kept behind
        // a symbol rather than deleted.
        //
        // The symbol means these lines are only compiled by a Debug build, so a
        // Debug build is part of the gate. A Release only check will not notice
        // when this block stops compiling.
#if DEBUG
        if (HandleDeveloperArguments(e.Args))
        {
            return;
        }
#endif

        // Ownership is taken with WaitOne rather than with the constructor's
        // initiallyOwned argument, because "created" only reports whether this
        // process made the named object. A replacement started by the in-app
        // restart opens the object the outgoing process already has a handle to,
        // so created comes back false and the app refuses to start, even though
        // the outgoing process has already given up ownership. created says
        // nothing about who holds it; WaitOne does.
        _mutex = new Mutex(false, "GamerToolSingleInstance");
        bool claimed;
        try
        {
            // Not zero. A restart launches the replacement while this process is
            // still tearing down, so the claim can still be in the outgoing
            // process's hands for a moment. A genuine double launch only waits
            // out this window and then gets the message.
            claimed = _mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            // The previous owner was killed without releasing. The wait has
            // already handed ownership over, so this instance is the one that
            // should carry on.
            claimed = true;
        }

        if (!claimed)
        {
            MessageBox.Show("GAMER TOOL IS ALREADY RUNNING", "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Opened before anything else can fail, so the first line in the file is
        // the one that explains a start that never got any further.
        AppLog.Info("start " + (typeof(App).Assembly.GetName().Version?.ToString() ?? "?")
            + " on " + Environment.OSVersion.VersionString
            + " " + (Environment.Is64BitProcess ? "x64" : "x86")
            + " args=" + SanitiseArgs(string.Join(' ', e.Args)));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyReset.Run();
        System.Windows.Application.Current.Exit += (_, _) => EmergencyReset.Run();

        base.OnStartup(e);

        try
        {
            // Before the window, and so before anything can ask for a preview
            // track. An older build copied eleven megabytes of preview audio out
            // to the profile folder and never deleted it; the audio is decoded
            // from the executable now, so this is only clearing up after the
            // builds that did litter.
            AudioPreviewPlayer.RemoveExtractedCopies();
        }
        catch (Exception ex)
        {
            WriteLog("PREVIEW CLEANUP", ex);
        }

        try
        {
            MainWindow window = new();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteLog("STARTUP", ex);
            MessageBox.Show(StartupFailureText(ex), "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// The text of the "could not start" box.
    /// <para>
    /// Both the top of the chain and the actual cause, because on its own either
    /// one sends you the wrong way. A real failure this was written for was a WPF
    /// template that would not parse: the box said "Provide value on
    /// 'System.Windows.StaticResourceExtension' threw an exception", which says a
    /// resource could not be found, and the cause two levels down was a null
    /// Binding in a trigger. Somebody reading only the first line goes looking for
    /// a missing resource, and there is no missing resource.
    /// </para>
    /// <para>
    /// The cause is only added when it says something different, so the common case
    /// of a one-level exception is not padded out, and the log path is always named
    /// because the box is not where the stack trace lives.
    /// </para>
    /// </summary>
    private static string StartupFailureText(Exception ex)
    {
        const string headline = "GAMER TOOL COULD NOT START";

        string text = headline + Environment.NewLine + ex.Message;

        Exception root = ex;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        if (!string.IsNullOrWhiteSpace(root.Message)
            && !string.Equals(root.Message, ex.Message, StringComparison.Ordinal))
        {
            text += Environment.NewLine + Environment.NewLine
                + "Actual cause:" + Environment.NewLine + root.Message;
        }

        return text + Environment.NewLine + Environment.NewLine
            + "Full details, including the stack trace:" + Environment.NewLine + AppLog.Path_;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("UI", e.Exception);
        e.Handled = true;

        // Not while the app is on its way out. A fault raised during teardown has
        // nothing to recover and nowhere to report to - the window may already be
        // gone - so a dialog is pure noise, and a modal on the exit path is worse
        // than noise because it can hold the process open.
        if (!FaultPolicy.ShouldShowDialog(SessionState.Current.ShuttingDown, isTerminating: false))
        {
            return;
        }

        ShowFault("SOMETHING WENT WRONG: " + e.Exception.Message);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }

        WriteLog("APP", ex);

        // This is where the one on quit came from. Unloading the AppDomain runs
        // every native module's uninitializer, and one of them threw a
        // DllNotFoundException: the log showed __scrt_uninitialize_type_info ->
        // _app_exit_callback -> SingletonDomainUnload, with no managed frame in
        // sight. It happens after the app has finished and the process is already
        // dying.
        //
        // Logged, not shown. The record belongs in the file, which is one click
        // away through diagnostics; a modal at this point tells the user nothing
        // they can act on and makes an ordinary quit look like a crash.
        if (!FaultPolicy.ShouldShowDialog(SessionState.Current.ShuttingDown, e.IsTerminating))
        {
            return;
        }

        ShowFault("SOMETHING WENT WRONG: " + ex.Message);
    }

    /// <summary>How long the same fault has to stay quiet before it is shown again.</summary>
    private const int FaultDialogCooldownSeconds = 30;

    private static readonly object FaultGate = new();

    private static string _lastFault = string.Empty;

    private static DateTime _lastFaultAt = DateTime.MinValue;

    /// <summary>
    /// Reports a fault to the user, at most once for any given fault in any
    /// thirty second window.
    /// <para>
    /// Every fault is written to the log without exception or limit - that is the
    /// record, and the diagnostics button can reach it. This is only about how
    /// often the same one interrupts.
    /// </para>
    /// <para>
    /// The dispatcher handler sets <c>e.Handled</c>, so the app carries on after a
    /// fault, which is right. The problem was what happens next: several timers
    /// run continuously - the gamma lock every 1.5 s, the state poll every 4 s,
    /// the spectrum at sixty frames a second - so a fault in a tick handler
    /// produced a modal box on every single tick. A modal on a timer is the worst
    /// of both worlds: it is unmissable, it is modal, and it recurs the instant it
    /// is dismissed. Thirty seconds of quiet is long enough to have read and copied
    /// it, and short enough that a genuinely different fault is not swallowed by
    /// the previous one's cooldown.
    /// </para>
    /// </summary>
    private static void ShowFault(string text)
    {
        if (!TryBeginFaultDialog(text, DateTime.UtcNow))
        {
            return;
        }

        MessageBox.Show(text, "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Whether this fault is due a dialog, recording the decision either way.
    /// <para>
    /// Split out from the box itself so the rule can be exercised without one:
    /// a test cannot show a modal, and a rule that can only be checked by causing
    /// a real fault in a real app is a rule that will quietly rot.
    /// </para>
    /// </summary>
    internal static bool TryBeginFaultDialog(string text, DateTime nowUtc)
    {
        lock (FaultGate)
        {
            if (string.Equals(text, _lastFault, StringComparison.Ordinal)
                && (nowUtc - _lastFaultAt).TotalSeconds < FaultDialogCooldownSeconds)
            {
                return false;
            }

            _lastFault = text;
            _lastFaultAt = nowUtc;
            return true;
        }
    }

#if DEBUG
    /// <summary>
    /// Runs and exits for the developer flags. Returns true when one of them was
    /// handled and startup should stop there.
    /// </summary>
    private bool HandleDeveloperArguments(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.Equals("--makeicon", StringComparison.OrdinalIgnoreCase))
            {
                string target = GetArg(args, "--makeicon") ?? "GamerTool.ico";
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = "GamerTool.ico";
                }

                try
                {
                    IconFactory.WriteToFile(target);
                    string preview = Path.ChangeExtension(target, ".preview.png");
                    IconFactory.WritePreview(preview, 256);
                    TraceLog.Write("ICON WROTE " + Path.GetFullPath(target) + " " + new FileInfo(target).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    TraceLog.Write("ICON", ex);
                }

                Shutdown(0);
                return true;
            }

            if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                Shutdown(0);
                return true;
            }
        }

        return false;
    }

    private static void RunSlotSelfTest()
    {
        try
        {
            GamerTool.Models.AppSettings sample = new();
            sample.AppProfiles.Add(new GamerTool.Models.AppProfile
            {
                Id = "app_old1",
                Name = "Counter-Strike 2",
                ExePath = @"C:\Games\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
                ProcessName = "cs2",
                DisplayPresetId = "camper",
                AudioPresetId = "footstep",
                Enabled = true,
                Source = "STEAM"
            });
            sample.AppProfiles.Add(new GamerTool.Models.AppProfile
            {
                Id = "app_old2",
                Name = "Apex Legends",
                ExePath = @"D:\SteamLibrary\steamapps\common\Apex\Apex.exe",
                ProcessName = "Apex",
                DisplayPresetId = "sniper",
                AudioPresetId = null!,
                Enabled = true,
                Source = "STEAM"
            });

            List<GamerTool.Models.HotkeySlot> slots = SlotService.Migrate(sample);
            TraceLog.Write("SLOTS " + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (GamerTool.Models.HotkeySlot slot in slots)
            {
                TraceLog.Write("  SLOT  " + slot.Name
                    + " | key=" + (string.IsNullOrWhiteSpace(slot.Hotkey) ? "none" : slot.Hotkey)
                    + " | screen=" + (slot.DisplayPresetId ?? "none")
                    + " | sound=" + (slot.AudioPresetId ?? "none")
                    + " | auto=" + slot.AutoActivate
                    + " | target=" + slot.TargetText
                    + " | " + slot.WorkText);
            }

            GamerTool.Models.HotkeySlot? byPath = SlotService.MatchForeground(
                slots,
                @"C:\Games\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
                "cs2");
            TraceLog.Write("MATCH BY PATH: " + (byPath?.Name ?? "none"));

            GamerTool.Models.HotkeySlot? byName = SlotService.MatchProcessName(slots, "Apex");
            TraceLog.Write("MATCH BY NAME: " + (byName?.Name ?? "none"));

            GamerTool.Models.HotkeySlot? miss = SlotService.MatchProcessName(slots, "chrome");
            TraceLog.Write("MATCH MISS: " + (miss?.Name ?? "none"));

            HashSet<string> names = SlotService.TargetProcessNames(slots);
            TraceLog.Write("WATCH NAMES: " + string.Join(",", names));
        }
        catch (Exception ex)
        {
            TraceLog.Write("SLOTS", ex);
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }


    private static void RunSelfTest()
    {
        TraceLog.Write("SELFTEST START");
        try
        {
            AppLibraryService library = new();
            IReadOnlyList<string> roots = library.SteamRoots();
            TraceLog.Write("STEAM ROOTS " + roots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string root in roots)
            {
                foreach (string lib in library.SteamLibraries(root))
                {
                    TraceLog.Write("LIBRARY " + lib);
                }
            }

            IReadOnlyList<GamerTool.Models.AppCandidate> steam = library.ScanSteam();
            TraceLog.Write("STEAM GAMES " + steam.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (GamerTool.Models.AppCandidate candidate in steam)
            {
                TraceLog.Write("  STEAM  " + candidate.Name + " -> " + candidate.ExePath);
            }

            IReadOnlyList<GamerTool.Models.AppCandidate> programs = library.ScanInstalledPrograms();
            TraceLog.Write("PROGRAMS " + programs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (GamerTool.Models.AppCandidate candidate in programs)
            {
                TraceLog.Write("  APP    " + candidate.Name + " -> " + candidate.ExePath);
            }

            AudioService audio = new();
            TraceLog.Write("FXSOUND INSTALLED " + audio.IsInstalled.ToString() + " RUNNING " + audio.IsRunning.ToString());

            RunSlotSelfTest();
            TraceLog.Write("SELFTEST DONE");
        }
        catch (Exception ex)
        {
            TraceLog.Write("SELFTEST", ex);
        }
    }

#endif

    /// <summary>
    /// The crash handler's way out. Goes to the same rolling log as everything
    /// else so there is one file to look at, and writes synchronously, because
    /// this runs on the way down and a buffered line would be lost.
    /// </summary>
    public static void WriteLog(string tag, Exception ex)
    {
        AppLog.Error(tag, ex);
    }


    /// <summary>Command line arguments, with the user profile taken out of them.</summary>
    private static string SanitiseArgs(string args) => AppLog.Sanitise(args);

    /// <summary>
    /// Gives up the single-instance claim without exiting, so a replacement
    /// process can start straight away instead of losing the race, finding the
    /// mutex held, telling the user Gamer Tool is already running and quitting.
    /// <para>
    /// Only the thread that created the mutex with ownership can release it, and
    /// that is the thread OnStartup ran on, which is the UI thread. So this has
    /// to be called from the UI thread. It is idempotent in practice: releasing
    /// twice throws, and that is caught here rather than taken as a failure.
    /// </para>
    /// </summary>
    internal void ReleaseSingleInstanceClaim()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held on this thread, so there was nothing to give up.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Hand the claim back on the way out rather than only closing the handle.
        // A process that was killed, or one that exits through a path that skips
        // here, otherwise leaves the next launch waiting out the abandoned
        // timeout before it can start.
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held on this thread.
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}


