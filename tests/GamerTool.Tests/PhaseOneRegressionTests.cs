using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The regressions from the adversarial review, asserted as behaviour.
/// <para>
/// Every one of these was a defect that a test reading the source would have
/// called correct: the code said the right thing in a comment and did the wrong
/// thing on the path. So these drive the state and the arithmetic, and the two
/// that need a window drive the extracted pure comparison instead of a real one.
/// </para>
/// </summary>
public class PhaseOneRegressionTests
{
    // ---------------------------------------------------------------
    // 1.1 - a wildcard that is applied must stay watched for exit.
    // ---------------------------------------------------------------

    /// <summary>
    /// The invariant, driven through the exact sequence that broke it.
    /// <para>
    /// ApplyWatchState is what any slot edit calls, and it clears the unbound
    /// watch twice over - once in Reset, once in PrimeProcesses. The defect was
    /// that nothing put it back, so a wildcard's exit revert was armed once and
    /// could never be re-armed.
    /// </para>
    /// </summary>
    [Fact]
    public void A_wildcard_watch_survives_a_slot_edit()
    {
        ProcessWatcherService watcher = new();
        List<string> exited = new();
        watcher.TargetExited += name => exited.Add(name);

        // Armed by a foreground match: the watcher is told about an unbound
        // process and it is now marked announced so no launch is raised for it.
        watcher.WatchUnbound("SomeGame");
        Assert.Contains("SomeGame", watcher.WatchedNamesForTest);

        // The user opens the app and edits an unrelated slot. This is what
        // ApplyWatchState does on both arms of a turn.
        watcher.Reset();
        watcher.PrimeProcesses(new[] { "SomeBoundGame" });

        // The re-arm that was missing. Without it the exit below never fires.
        Assert.Equal("SomeGame", watcher.WatchUnbound("SomeGame"));

        Assert.Contains("SomeGame", watcher.WatchedNamesForTest);
        Assert.Contains("SomeBoundGame", watcher.TrackedNamesForTest);
    }

    [Fact]
    public void A_wildcard_watch_raised_before_the_edit_still_reports_its_exit()
    {
        // The end-to-end consequence. With the re-arm, a game that closes after a
        // slot edit still raises its exit; without it the profile stayed applied to
        // the desktop with nothing able to take it back.
        ProcessWatcherService watcher = new();
        List<string> exited = new();
        watcher.TargetExited += name => exited.Add(name);

        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");
        watcher.Reset();
        watcher.PrimeProcesses(new[] { "SomeBoundGame" });
        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");

        watcher.ScanForLaunches();

        Assert.Contains("GamerToolTestProcessThatIsNotRunning", exited);
    }

    [Fact]
    public void Only_one_wildcard_process_is_watched_and_a_refusal_is_visible()
    {
        // WatchUnbound returns empty when a different process is already watched,
        // which is the refusal the review flagged as being discarded. It is the
        // answer that says "this game is applied but unmonitored", so it has to be
        // checkable rather than dropped on the floor.
        ProcessWatcherService watcher = new();

        Assert.Equal("first", watcher.WatchUnbound("first"));
        Assert.Equal(string.Empty, watcher.WatchUnbound("second"));

        // And the only way out is the one the re-arm path uses.
        watcher.ForgetUnbound("first");
        Assert.Equal("second", watcher.WatchUnbound("second"));
    }

    [Fact]
    public void A_re_armed_watch_does_not_raise_a_launch()
    {
        // Re-arming after a slot edit must not look like a game starting. The whole
        // point of the re-arm is to restore monitoring for something already
        // running, and announcing it would start a second apply.
        ProcessWatcherService watcher = new();
        int launches = 0;
        watcher.TargetLaunched += _ => launches++;

        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");
        watcher.Reset();
        watcher.PrimeProcesses(Array.Empty<string>());
        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");
        watcher.ScanForLaunches();

        Assert.Equal(0, launches);
    }

    // ---------------------------------------------------------------
    // 2.3 - one failing revert must not cost the others their revert.
    // ---------------------------------------------------------------

    /// <summary>
    /// The reachability note, because it shapes this test.
    /// <para>
    /// A batch of exits in one scan needs several announced-then-gone names, and
    /// a wildcard only ever holds one unbound process - so the batch is a *bound*
    /// case: two games running, both gone between two scans. That cannot be built
    /// without spawning and killing real processes, so what is asserted here is
    /// the property the per-item try/catch buys: a handler that throws is contained,
    /// and the sweep still finishes its own bookkeeping.
    /// </para>
    /// </summary>
    [Fact]
    public void A_throwing_exit_handler_does_not_escape_the_scan()
    {
        // A revert can throw - it touches WPF, saves the profile synchronously and
        // marshals a hardware write onto a worker. Before the per-item try, that
        // propagated out of ScanForLaunches into the scan's outer catch, which logs
        // one line naming a scan and abandons the rest of the loop.
        ProcessWatcherService watcher = new();
        int raised = 0;

        watcher.TargetExited += _ =>
        {
            raised++;
            throw new InvalidOperationException("the ramp would not come off");
        };

        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");

        watcher.ScanForLaunches();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_throwing_exit_handler_does_not_leave_the_name_watched()
    {
        // The other half. The name is released before the event is raised, so a
        // handler that throws on its way out cannot leave a dead process in the
        // watch set - which would have raised the same exit again on every
        // subsequent scan for the rest of the session.
        ProcessWatcherService watcher = new();
        int raised = 0;

        watcher.TargetExited += _ =>
        {
            raised++;
            throw new InvalidOperationException("the ramp would not come off");
        };

        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");

        watcher.ScanForLaunches();
        watcher.ScanForLaunches();
        watcher.ScanForLaunches();

        // Once, not three times.
        Assert.Equal(1, raised);
        Assert.Empty(watcher.WatchedNamesForTest);
    }

    [Fact]
    public void A_scan_still_walks_the_rest_of_the_list_after_a_throw()
    {
        // A throw must cost that handler and nothing else. Provable with one
        // watcher because the bookkeeping happens before the raise: the name is
        // removed, then the handler is called, then it throws. So the sweep's own
        // state has to be correct afterwards even though the handler never
        // completed - which is exactly what lets the next scan run clean instead
        // of raising the same exit forever.
        ProcessWatcherService watcher = new();
        List<string> exited = new();

        watcher.TargetExited += name =>
        {
            exited.Add(name);
            throw new InvalidOperationException("boom");
        };

        watcher.PrimeProcesses(new[] { "KnownGame" });
        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");

        watcher.ScanForLaunches();
        exited.Clear();

        watcher.ScanForLaunches();

        Assert.Empty(exited);
        Assert.Contains("KnownGame", watcher.TrackedNamesForTest);
    }

    // ---------------------------------------------------------------
    // 1.2 - the fullscreen geometry, as arithmetic.
    // ---------------------------------------------------------------

    private static ProcessWatcherService.Rect R(int l, int t, int r, int b) =>
        new() { Left = l, Top = t, Right = r, Bottom = b };

    /// <summary>A 1920x1080 monitor at the origin, which is every real case.</summary>
    private static ProcessWatcherService.Rect Monitor() => R(0, 0, 1920, 1080);

    [Fact]
    public void An_exact_fullscreen_window_covers_the_monitor()
    {
        Assert.True(ProcessWatcherService.RectCoversMonitor(Monitor(), Monitor()));
    }

    [Fact]
    public void A_window_larger_than_the_monitor_covers_it()
    {
        // Exclusive fullscreen on several drivers reports a rectangle larger than the
        // display, by the border width on each side. A cap on outward extension
        // would reject exactly the case the feature exists for.
        Assert.True(ProcessWatcherService.RectCoversMonitor(R(-8, -8, 1928, 1088), Monitor()));
    }

    [Fact]
    public void A_window_extending_far_outward_still_covers_it()
    {
        // Unbounded outward, deliberately. Spanning two monitors is still covering
        // this one.
        Assert.True(ProcessWatcherService.RectCoversMonitor(R(-1920, 0, 1920, 1080), Monitor()));
    }

    [Fact]
    public void A_window_inset_by_a_border_shrinking_shadow_still_covers_it()
    {
        // DwmGetWindowAttribute's extended frame bounds sit inside GetWindowRect by
        // the shadow width, and DPI rounding at 125% or 150% quantises an edge. This
        // is what the one pixel tolerance failed and the sixteen pixel one allows.
        Assert.True(ProcessWatcherService.RectCoversMonitor(R(7, 7, 1913, 1073), Monitor()));
    }

    [Fact]
    public void The_tolerance_is_the_full_sixteen_pixels()
    {
        // The boundary, in the accepting direction, so the constant cannot be
        // quietly reduced back to something a shadow defeats.
        int slack = ProcessWatcherService.MonitorSlack;
        Assert.Equal(16, slack);
        Assert.True(ProcessWatcherService.RectCoversMonitor(R(slack, slack, 1920 - slack, 1080 - slack), Monitor()));
    }

    [Fact]
    public void A_window_inset_by_more_than_the_tolerance_is_not_fullscreen()
    {
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(17, 17, 1903, 1063), Monitor()));
    }

    [Fact]
    public void A_maximised_window_is_not_fullscreen()
    {
        // The case the whole test exists for: a maximised window stops at the work
        // area and leaves the taskbar showing. A wildcard that treated this as
        // fullscreen would apply a game profile to the user's browser.
        //
        // 40px taskbar, so the window's bottom edge is 40 short of the monitor's.
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(0, 0, 1920, 1040), Monitor()));
    }

    [Fact]
    public void A_maximised_window_on_a_secondary_monitor_is_not_fullscreen()
    {
        // The same, offset, because the test that only ever uses the origin catches
        // an implementation that forgot to subtract the monitor's position.
        ProcessWatcherService.Rect right = R(1920, 0, 3840, 1080);
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(1920, 0, 3840, 1040), right));
        Assert.True(ProcessWatcherService.RectCoversMonitor(right, right));
    }

    [Fact]
    public void A_degenerate_rectangle_never_covers_anything()
    {
        // What GetWindowRect returns for a minimised or off-screen window. Without
        // this a zero sized window compares equal to nothing at all.
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(0, 0, 0, 0), Monitor()));
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(100, 100, 100, 100), Monitor()));
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(500, 500, 100, 100), Monitor()));
    }

    [Fact]
    public void A_window_offset_onto_a_second_monitor_does_not_cover_the_first()
    {
        // Straddling the seam without covering the first monitor's far edge.
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(1000, 0, 3000, 1080), Monitor()));
    }

    [Fact]
    public void The_tolerance_is_not_wide_enough_to_admit_a_normal_window()
    {
        // Sweeps width rather than picking cases. The false-positive half of a
        // tolerance is the one that applies a game preset to a document, so the
        // number is pinned against a window that is merely large, not merely small.
        int admitted = 0;

        for (int inset = 0; inset <= 40; inset++)
        {
            if (ProcessWatcherService.RectCoversMonitor(
                    R(inset, inset, 1920 - inset, 1080 - inset),
                    Monitor()))
            {
                admitted++;
            }
        }

        // Insets 0..16 inclusive are accepted, 17..40 are not.
        Assert.Equal(ProcessWatcherService.MonitorSlack + 1, admitted);
    }

    [Fact]
    public void A_taskbar_taller_than_the_tolerance_keeps_a_maximised_window_out()
    {
        // The real discriminator, and why the tolerance is safe at sixteen. A
        // maximised window's shortfall from the monitor is the taskbar height, and
        // every normal taskbar is taller than that - the live-window test in
        // FullscreenDetectionTests confirms it on a real desktop.
        Assert.False(ProcessWatcherService.RectCoversMonitor(R(0, 0, 1920, 1080 - 48), Monitor()));
    }

    [Fact]
    public void An_auto_hiding_taskbar_cannot_be_told_from_fullscreen_by_geometry()
    {
        // A known limitation, pinned so it is a decision rather than a surprise.
        //
        // With the taskbar set to auto-hide the work area *is* the monitor
        // rectangle, so a maximised browser and a fullscreen game are the same four
        // integers and no comparison can separate them. Widening the tolerance did
        // not create this - a one pixel slack had the same outcome - and nothing in
        // Win32 reports per-monitor auto-hide reliably enough to key off.
        //
        // What this means for the feature: a user with the taskbar auto-hidden who
        // maximises a browser will get the wildcard. Worth stating plainly, because
        // the alternative reading of "a browser never matches" is not true.
        Assert.True(ProcessWatcherService.RectCoversMonitor(R(0, 0, 1920, 1080), Monitor()));

        // The mitigation that does work, and is already in place: the window must
        // be the foreground window, and Read() refuses the shell hosts that own the
        // taskbar and the start menu.
        Assert.NotNull(typeof(ProcessWatcherService).GetMethod("Read"));
    }

    // ---------------------------------------------------------------
    // 3.1 - the apply debounce, as arithmetic. The point of the
    // extraction is that a cleared guard and an ancient one must not
    // be the same value.
    // ---------------------------------------------------------------

    [Fact]
    public void A_brand_new_claim_is_inside_the_debounce_window()
    {
        DateTime now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(AutoApplyGate.IsDebounced(now, now));
    }

    [Fact]
    public void A_claim_just_inside_the_window_is_still_debounced()
    {
        // The exact case the defect produced. A game closed, its guard was stood
        // down without clearing the stamp, and it was relaunched 1.9s later.
        DateTime now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime stamp = now.AddMilliseconds(-1900);

        Assert.True(AutoApplyGate.IsDebounced(stamp, now));
    }

    [Fact]
    public void A_claim_past_the_window_is_not_debounced()
    {
        DateTime now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime stamp = now.AddMilliseconds(-2001);

        Assert.False(AutoApplyGate.IsDebounced(stamp, now));
    }

    [Fact]
    public void A_stand_down_clears_the_guard_so_an_immediate_relaunch_applies()
    {
        // The fix itself. Every stand-down path writes the sentinel, and the
        // sentinel is not debounced at any elapsed time - which is what makes a
        // relaunch inside two seconds work, and what the exit path failed to do.
        DateTime now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime cleared = DateTime.MinValue;

        Assert.False(AutoApplyGate.IsDebounced(cleared, now));

        // Not even one millisecond later, which is the case that was broken.
        Assert.False(AutoApplyGate.IsDebounced(cleared, now.AddMilliseconds(1)));
        Assert.False(AutoApplyGate.IsDebounced(cleared, now.AddSeconds(1)));
    }

    [Fact]
    public void The_window_is_two_seconds_and_not_anything_else()
    {
        // Pinned because the number was implicitly hard-coded and the fix depends on
        // it: a claim is debounced strictly inside two seconds and not at it.
        Assert.Equal(TimeSpan.FromSeconds(2), AutoApplyGate.Debounce);

        DateTime now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(AutoApplyGate.IsDebounced(now.AddMilliseconds(-1999), now));
        Assert.False(AutoApplyGate.IsDebounced(now.AddMilliseconds(-2000), now));
    }

    [Fact]
    public void A_clock_that_has_moved_backwards_does_not_lock_the_guard_closed()
    {
        // UtcNow is not monotonic: an NTP correction or a resume from sleep can put
        // it behind the stamp. A negative elapsed time is less than the window, so
        // the naive comparison would debounce the claim for as long as the skew
        // lasts - which on a laptop that slept is exactly the situation where the
        // user comes back and expects the game they just launched to apply.
        DateTime now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime ahead = now.AddSeconds(30);

        Assert.False(AutoApplyGate.IsDebounced(ahead, now));
    }

    // ---------------------------------------------------------------
    // 3.4 - the device suffix match, as behaviour.
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true)]
    [InlineData(@"\\.\DISPLAY2", "DISPLAY2", true)]
    [InlineData("DISPLAY2", @"\\.\DISPLAY2", false)]
    [InlineData(@"\\.\DISPLAY20", "DISPLAY2", false)]
    [InlineData(@"\\.\DISPLAY21", "DISPLAY2", false)]
    [InlineData(@"\\.\DISPLAY12", "DISPLAY2", false)]
    [InlineData(@"\\.\DISPLAY2", "DISPLAY20", false)]
    [InlineData(@"\\.\DISPLAY2", "2", false)]
    [InlineData(@"\\.\DISPLAY2", "", false)]
    public void A_suffix_match_has_to_land_on_a_delimiter(string device, string wanted, bool expected)
    {
        Assert.Equal(expected, DisplayService.DeviceMatches(device, wanted));
    }

    [Fact]
    public void A_stored_display_two_cannot_claim_display_twenty()
    {
        // The collision the loose EndsWith allowed, and it failed in the worst
        // direction: the app reported a successful scoped push while writing to a
        // screen the user had never pointed it at.
        Assert.False(DisplayService.DeviceMatches(@"\\.\DISPLAY20", "DISPLAY2"));
        Assert.False(DisplayService.DeviceMatches(@"\\.\DISPLAY200", "DISPLAY2"));
        Assert.False(DisplayService.DeviceMatches(@"\\.\DISPLAY21", "DISPLAY2"));

        // And the genuine match still works, or the fix has just broken scoping.
        Assert.True(DisplayService.DeviceMatches(@"\\.\DISPLAY2", "DISPLAY2"));
    }

    [Fact]
    public void Matching_ignores_case_but_still_respects_boundaries()
    {
        Assert.True(DisplayService.DeviceMatches(@"\\.\DISPLAY2", @"\\.\display2"));
        Assert.False(DisplayService.DeviceMatches(@"\\.\DISPLAY20", @"\\.\DISPLAY2"));
    }

    [Fact]
    public void A_partial_name_inside_a_longer_device_is_never_a_match()
    {
        // The other half of the collision: a wanted string that is not itself a
        // whole trailing segment. "2" against DISPLAY2 ends with a "2" and must not
        // match anything.
        for (int n = 0; n <= 30; n++)
        {
            Assert.False(DisplayService.DeviceMatches(@"\\.\DISPLAY2", n.ToString()));
        }
    }
}