using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The three background polls that were doing more than they needed to: the
/// FxSound status read, the launch scan, and the gamma lock.
/// </summary>
public class PollingCleanupTests
{
    // ---------------------------------------------------------------
    // The engine poll runs slowly when nobody can see the answer.
    // ---------------------------------------------------------------

    [Fact]
    public void The_engine_poll_is_fast_on_screen_and_slow_when_hidden()
    {
        Assert.Equal(FxPollPolicy.Visible, FxPollPolicy.IntervalFor(true));
        Assert.Equal(FxPollPolicy.Hidden, FxPollPolicy.IntervalFor(false));
    }

    [Fact]
    public void Hidden_is_much_slower_than_visible_but_not_so_slow_it_stops_being_a_net()
    {
        // The visible rate is what the status line needs. Hidden has to be well
        // under it to be worth having, and still short enough to notice an engine
        // that has lost the curve while a game is running.
        Assert.True(FxPollPolicy.Hidden >= FxPollPolicy.Visible * 8);
        Assert.True(FxPollPolicy.Hidden <= TimeSpan.FromSeconds(60));
    }

    // ---------------------------------------------------------------
    // The launch scan looks up the watched names, not every process.
    // ---------------------------------------------------------------

    private static string SelfName => Process.GetCurrentProcess().ProcessName;

    [Fact]
    public void A_running_watched_process_is_announced_once_with_its_real_path()
    {
        ProcessWatcherService watcher = new();
        List<WatchedWindow> launched = new();
        watcher.TargetLaunched += launched.Add;

        watcher.PrimeProcesses(new[] { SelfName });
        watcher.ScanForLaunches();
        watcher.ScanForLaunches();

        WatchedWindow only = Assert.Single(launched);
        Assert.Equal(SelfName, only.ProcessName, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(only.ExePath));
        Assert.Equal(
            Path.GetFileNameWithoutExtension(only.ExePath),
            only.ProcessName);
    }

    [Fact]
    public void A_watched_name_matches_whatever_its_case()
    {
        ProcessWatcherService watcher = new();
        int launches = 0;
        watcher.TargetLaunched += _ => launches++;

        watcher.PrimeProcesses(new[] { SelfName.ToUpperInvariant() });
        watcher.ScanForLaunches();

        Assert.Equal(1, launches);
    }

    [Fact]
    public void A_process_that_is_not_running_is_never_announced()
    {
        ProcessWatcherService watcher = new();
        int launches = 0;
        watcher.TargetLaunched += _ => launches++;

        watcher.PrimeProcesses(new[] { "GamerToolTestProcessThatIsNotRunning" });
        watcher.ScanForLaunches();

        Assert.Equal(0, launches);
    }

    [Fact]
    public void A_launch_handler_that_changes_the_watch_does_not_break_the_scan()
    {
        // The names are copied before the loop for exactly this: a handler is free
        // to call back in and change the lists the scan is walking.
        ProcessWatcherService watcher = new();
        int launches = 0;

        watcher.TargetLaunched += _ =>
        {
            launches++;
            watcher.Reset();
            watcher.PrimeProcesses(new[] { SelfName });
        };

        watcher.PrimeProcesses(new[] { SelfName, "GamerToolTestProcessThatIsNotRunning" });

        Exception? thrown = Record.Exception(watcher.ScanForLaunches);

        Assert.Null(thrown);
        Assert.Equal(1, launches);
    }

    // ---------------------------------------------------------------
    // The gamma lock compares tables instead of writing blindly.
    // ---------------------------------------------------------------

    private static ushort[] Table(Func<int, int> f)
    {
        ushort[] t = new ushort[256];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = (ushort)Math.Clamp(f(i), 0, 65535);
        }

        return t;
    }

    [Fact]
    public void Identical_tables_match()
    {
        ushort[] a = Table(i => i * 257);
        Assert.True(DisplayService.RampsMatch(a, (ushort[])a.Clone(), 128));
    }

    [Fact]
    public void Small_noise_is_not_drift()
    {
        ushort[] a = Table(i => i * 257);
        ushort[] b = Table(i => i * 257 + 100);

        Assert.True(DisplayService.RampsMatch(a, b, 128));
    }

    [Fact]
    public void Another_program_resetting_the_ramp_is_drift()
    {
        // A boosted table against the identity ramp a game restores.
        ushort[] boosted = Table(i => (int)(Math.Pow(i / 255.0, 0.7) * 65535));
        ushort[] identity = Table(i => i * 257);

        Assert.False(DisplayService.RampsMatch(boosted, identity, 128));
    }

    [Fact]
    public void One_changed_entry_is_enough_to_count()
    {
        ushort[] a = Table(i => i * 257);
        ushort[] b = (ushort[])a.Clone();
        b[200] = (ushort)(b[200] + 4000);

        Assert.False(DisplayService.RampsMatch(a, b, 128));
    }

    [Fact]
    public void Tables_of_different_length_never_match()
    {
        Assert.False(DisplayService.RampsMatch(new ushort[256], new ushort[128], 128));
    }
}
