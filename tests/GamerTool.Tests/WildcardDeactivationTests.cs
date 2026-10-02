using System;
using System.Collections.Generic;
using System.Diagnostics;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The wildcard's deactivation, on both paths.
/// <para>
/// Two ways a wildcard can be applied and neither of them had a working undo. The
/// process-exit branch was written and could never run, because the process it
/// needed to watch was never registered with the scan that detects exits. The
/// focus-loss branch did not exist at all, so the slot applied on a fullscreen game
/// and stayed applied on whatever the user alt-tabbed to next - and would not
/// re-apply either, because a browser is not fullscreen.
/// </para>
/// <para>
/// These tests drive the watcher's registration and exit detection directly rather
/// than through a window, because the question is "does an exit get noticed", not
/// "what does the screen look like afterwards".
/// </para>
/// </summary>
public class WildcardDeactivationTests
{
    /// <summary>
    /// The name of this test process, which is certainly running and certainly not
    /// a game. Used as the stand-in for a wildcard's matched process: the watcher
    /// cannot tell one running process from another, and a test that started and
    /// killed a real game would be both slow and flaky.
    /// </summary>
    private static string SelfName
    {
        get
        {
            using Process me = Process.GetCurrentProcess();
            return me.ProcessName;
        }
    }

    private static ProcessWatcherService Watcher() => new();

    /// <summary>
    /// A wildcard-matched process is watched for exit even though no slot is
    /// bound to it.
    /// <para>
    /// This is the whole bug. The watch list is built from the slots, and a
    /// wildcard contributes nothing to it by design, so its process was never in
    /// _knownProcesses - which meant never in the alive set, which meant never in
    /// the exit list. The revert code below that was complete and unreachable.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unbound_process_can_be_watched_for_exit()
    {
        ProcessWatcherService watcher = Watcher();

        string watched = watcher.WatchUnbound(SelfName);

        Assert.Equal(SelfName, watched);
    }

    /// <summary>
    /// A wildcard armed with no other game bound still gets a scan.
    /// <para>
    /// The scan returned early on an empty watch list. A wildcard contributes
    /// nothing to that list, so the only-armed-wildcard case - which is the common
    /// one for someone who installed this feature precisely because they had
    /// nothing bound - never ran the scan at all.
    /// </para>
    /// </summary>
    [Fact]
    public void The_scan_runs_for_a_wildcard_with_nothing_else_watched()
    {
        ProcessWatcherService watcher = Watcher();
        bool exited = false;

        // Nothing primed: no slot-derived names at all, exactly the state a
        // wildcard-only setup produces.
        Assert.Empty(watcher.WatchedNamesForTest);

        watcher.TargetExited += _ => exited = true;
        watcher.WatchUnbound(SelfName);
        watcher.ScanForLaunches();

        // No exit is expected - the process is running - but the scan has to have
        // executed to have known that. A scan that returned early would also report
        // nothing, so this asserts the process is being tracked rather than the
        // absence of an exit.
        Assert.Contains(SelfName, watcher.TrackedNamesForTest);
        Assert.False(exited);
    }

    /// <summary>
    /// The exit fires when a watched process actually goes.
    /// <para>
    /// Driven through the real scan with the name forced to something that cannot
    /// be running. Everything else is genuine: real Process.GetProcesses, real
    /// name comparison, real exit detection. Only the name is fictional, because a
    /// test that started and killed a process would be slow and racy.
    /// </para>
    /// </summary>
    [Fact]
    public void A_watched_process_that_goes_raises_its_exit()
    {
        ProcessWatcherService watcher = Watcher();
        List<string> exited = new();
        watcher.TargetExited += name => exited.Add(name);

        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");
        watcher.ScanForLaunches();

        Assert.Contains("GamerToolTestProcessThatIsNotRunning", exited);
    }

    /// <summary>
    /// Watching marks the process as already announced, so the next scan does not
    /// raise a launch for it.
    /// <para>
    /// The wildcard matched a program that was already running - the app saw it on
    /// the foreground window after it appeared, not as it started. Raising a launch
    /// for it would run a second apply for something already applied, and on the
    /// bound path it would also be a lie about what the watcher observed.
    /// </para>
    /// </summary>
    [Fact]
    public void Watching_an_unbound_process_does_not_also_announce_a_launch()
    {
        ProcessWatcherService watcher = Watcher();
        int launches = 0;
        watcher.TargetLaunched += _ => launches++;

        watcher.WatchUnbound(SelfName);
        watcher.ScanForLaunches();

        Assert.Equal(0, launches);
    }

    /// <summary>
    /// Only one unbound process is watched at a time.
    /// <para>
    /// There is only ever one wildcard, so it can only have claimed one program.
    /// A second would leave the first's exit unmonitored while the revert was keyed
    /// to the second, which is worse than watching neither.
    /// </para>
    /// </summary>
    [Fact]
    public void A_second_unbound_process_is_not_watched()
    {
        ProcessWatcherService watcher = Watcher();

        watcher.WatchUnbound("first");
        string second = watcher.WatchUnbound("second");

        Assert.Equal(string.Empty, second);
        Assert.Equal(new[] { "first" }, watcher.WatchedNamesForTest);
    }

    /// <summary>
    /// Watching the same process twice is not a conflict.
    /// <para>
    /// The focus path and the apply path can both reach this: alt-tab away and back
    /// re-applies, and re-applying asks for the watch again. Treating that as a
    /// second process would silently stop watching the game it is currently on.
    /// </para>
    /// </summary>
    [Fact]
    public void Watching_the_same_process_twice_is_harmless()
    {
        ProcessWatcherService watcher = Watcher();

        Assert.Equal("game", watcher.WatchUnbound("game"));
        Assert.Equal("game", watcher.WatchUnbound("game"));

        Assert.Equal(new[] { "game" }, watcher.WatchedNamesForTest);
    }

    [Fact]
    public void Releasing_an_unbound_process_stops_watching_it()
    {
        ProcessWatcherService watcher = Watcher();
        watcher.WatchUnbound("game");

        watcher.ForgetUnbound("game");

        Assert.Empty(watcher.WatchedNamesForTest);
    }

    /// <summary>
    /// A released process stops raising exits.
    /// <para>
    /// Reached when the user alt-tabs away from a still-running game: the preset is
    /// stood down but the game has not quit. Its eventual exit must not raise a
    /// second revert for a preset that had already been undone.
    /// </para>
    /// </summary>
    [Fact]
    public void A_released_process_raises_no_further_exit()
    {
        ProcessWatcherService watcher = Watcher();
        List<string> exited = new();
        watcher.TargetExited += name => exited.Add(name);

        watcher.WatchUnbound("GamerToolTestProcessThatIsNotRunning");
        watcher.ForgetUnbound("GamerToolTestProcessThatIsNotRunning");
        watcher.ScanForLaunches();

        Assert.Empty(exited);
    }

    /// <summary>
    /// Reset clears the unbound watch too.
    /// <para>
    /// Reset is what ApplyWatchState calls on both arms of a turn, so an unbound
    /// watch that survived it would keep raising exits for a program the app had
    /// stopped tracking - and the exit handler would revert a preset the user had
    /// already moved on from.
    /// </para>
    /// </summary>
    [Fact]
    public void Reset_clears_the_unbound_watch()
    {
        ProcessWatcherService watcher = Watcher();
        watcher.WatchUnbound("game");

        watcher.Reset();

        Assert.Empty(watcher.WatchedNamesForTest);
    }

    /// <summary>
    /// An unbound watch does not survive re-priming.
    /// <para>
    /// The same sweep, reached by editing any slot. A wildcard's process is
    /// transient - it is whatever game happened to be fullscreen - so it must not
    /// outlive the watch list being rebuilt.
    /// </para>
    /// </summary>
    [Fact]
    public void Priming_the_watch_list_drops_the_unbound_process()
    {
        ProcessWatcherService watcher = Watcher();
        watcher.WatchUnbound("game");

        watcher.PrimeProcesses(new[] { "known" });

        Assert.Empty(watcher.WatchedNamesForTest);
        Assert.Contains("known", watcher.TrackedNamesForTest);
    }

    /// <summary>
    /// A blank name is refused rather than watched.
    /// </summary>
    [Fact]
    public void A_blank_name_is_not_watched()
    {
        ProcessWatcherService watcher = Watcher();

        Assert.Equal(string.Empty, watcher.WatchUnbound(string.Empty));
        Assert.Equal(string.Empty, watcher.WatchUnbound("   "));
        Assert.Empty(watcher.WatchedNamesForTest);
    }

    /// <summary>
    /// Unbound watching survives alongside the ordinary watch list.
    /// <para>
    /// Both can be live at once: a user who has bound three games and added a
    /// wildcard has four things to react to. Neither set may displace the other.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unbound_process_is_tracked_alongside_the_watch_list()
    {
        ProcessWatcherService watcher = Watcher();
        watcher.PrimeProcesses(new[] { "known" });
        watcher.WatchUnbound(SelfName);

        watcher.ScanForLaunches();

        Assert.Contains("known", watcher.TrackedNamesForTest);
        Assert.Contains(SelfName, watcher.TrackedNamesForTest);
    }
}