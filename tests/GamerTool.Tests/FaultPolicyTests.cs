using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Quitting the app must not look like a crash.
/// <para>
/// The report was a dialog saying "SOMETHING WENT WRONG: Dll was not found.",
/// sometimes, on the way out through the tray. The log had it: a
/// <see cref="DllNotFoundException"/> whose entire stack was native - the C
/// runtime finalising type information, through a module uninitializer, during
/// AppDomain unload - with not one managed frame in it. It arrived after
/// EmergencyReset had already run, so the app had finished, and it arrived with
/// IsTerminating set, because the process was dying of it.
/// </para>
/// <para>
/// Nothing about that is actionable and all of it is alarming, and a dialog raised
/// while the runtime is terminating is how a failing exit turns into a hung one.
/// The record still goes to the log; what changed is that the user is not
/// interrupted by teardown noise.
/// </para>
/// </summary>
public class FaultPolicyTests
{
    [Fact]
    public void A_fault_while_the_app_is_running_is_shown()
    {
        // The other side of the decision. Suppressing teardown noise must not
        // quietly turn into suppressing real problems.
        Assert.True(FaultPolicy.ShouldShowDialog(shuttingDown: false, isTerminating: false));
    }

    [Fact]
    public void A_fault_raised_while_shutting_down_is_not_shown()
    {
        Assert.False(FaultPolicy.ShouldShowDialog(shuttingDown: true, isTerminating: false));
    }

    [Fact]
    public void A_fault_that_is_taking_the_process_down_is_not_shown()
    {
        // A modal raised while the runtime is terminating can block the very
        // process exit it is reporting on. This holds even when the app thinks it
        // is still running, which is the case the shutdown flag alone would miss.
        Assert.False(FaultPolicy.ShouldShowDialog(shuttingDown: false, isTerminating: true));
    }

    [Fact]
    public void The_exact_case_from_the_log_is_not_shown()
    {
        // Spell out the real combination: a clean quit, and a fault arriving from
        // the native uninitizer on its way out.
        Assert.False(FaultPolicy.ShouldShowDialog(shuttingDown: true, isTerminating: true));
    }

    [Fact]
    public void Shutting_down_is_set_on_the_session_before_the_fault_handlers_need_it()
    {
        // The flag has to be reachable from a static handler, which means process
        // wide. A field on the window would be invisible to exactly the code that
        // needs to read it.
        SessionState state = SessionState.Current;
        bool before = state.ShuttingDown;

        try
        {
            state.ShuttingDown = true;
            Assert.True(state.ShuttingDown);
        }
        finally
        {
            state.ShuttingDown = before;
        }
    }

    [Fact]
    public void Both_fault_handlers_consult_the_policy_rather_than_deciding_for_themselves()
    {
        // The decision lives in one place. Two inline copies of the same judgement
        // is how the dispatcher one and the domain one end up disagreeing, and only
        // one of them getting fixed.
        string app = File.ReadAllText(FindApp("App.xaml.cs"));

        int uses = 0;
        int at = 0;
        while (true)
        {
            int found = app.IndexOf("FaultPolicy.ShouldShowDialog", at, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            uses++;
            at = found + 1;
        }

        Assert.Equal(2, uses);
    }

    [Fact]
    public void The_shutdown_flag_is_set_on_the_quit_path()
    {
        // A flag nothing sets is a flag that never suppresses anything.
        string window = File.ReadAllText(FindApp("MainWindow.xaml.cs"));
        string setup = File.ReadAllText(FindApp("MainWindow.Setup.cs"));

        Assert.Contains("ShuttingDown = true", window, StringComparison.Ordinal);
        Assert.Contains("ShuttingDown = true", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void Hiding_to_the_tray_does_not_count_as_shutting_down()
    {
        // The close-to-tray branch is not an exit. If the flag were set there, the
        // first fault after the window was hidden would be swallowed for a session
        // that is still running.
        string setup = File.ReadAllText(FindApp("MainWindow.Setup.cs"));

        int tray = setup.IndexOf("HideToTray();", StringComparison.Ordinal);
        int flag = setup.IndexOf("ShuttingDown = true", StringComparison.Ordinal);

        Assert.True(tray >= 0, "the close-to-tray branch is gone");
        Assert.True(flag > tray, "the shutdown flag is set before the close-to-tray branch returns");
    }

    [Fact]
    public void A_fault_is_still_written_to_the_log_when_it_is_not_shown()
    {
        // The record is the point. Suppressing the dialog must not throw away the
        // evidence, or this becomes hiding a fault rather than declining to
        // interrupt about it.
        string app = File.ReadAllText(FindApp("App.xaml.cs"));

        // Scoped to the domain handler, because the dispatcher handler's policy
        // call appears earlier in the file and comparing against that one would be
        // comparing the wrong two lines.
        // Anchored on the definition, not the name. The name also appears on the
        // subscription in OnStartup, which is above the dispatcher handler, and
        // scoping to that would sweep the dispatcher handler in with it.
        int handler = app.IndexOf(
            "private static void OnDomainUnhandledException",
            StringComparison.Ordinal);
        Assert.True(handler >= 0, "the domain handler is gone");

        string body = app.Substring(handler);

        int log = body.IndexOf("WriteLog(\"APP\", ex);", StringComparison.Ordinal);
        int policy = body.IndexOf("FaultPolicy.ShouldShowDialog", StringComparison.Ordinal);

        Assert.True(log >= 0, "the domain handler no longer logs");
        Assert.True(policy >= 0, "the domain handler no longer consults the policy");
        Assert.True(log < policy, "the domain handler decides whether to show it before recording it");
    }

    private static string FindApp(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "app", fileName);
    }
}
