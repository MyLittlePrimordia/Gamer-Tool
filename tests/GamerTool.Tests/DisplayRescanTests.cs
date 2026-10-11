using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The re-scan path, and the reason the enumeration alone is not enough.
/// <para>
/// The defect being guarded against is not that the display list is stale - it is
/// that a stale list is invisible. Every internal caller of the scan is guarded on
/// <c>Monitors.Count &gt; 0</c>, so the single enumeration at startup made every
/// later one unreachable, and nothing anywhere said so. A docked laptop therefore
/// kept yesterday's display list for the rest of the session: a newly attached
/// screen was never boosted, every slot's "which monitor" dropdown offered a
/// choice that no longer existed, and the gamma ramp the user had just applied
/// sat gone because Windows resets it on a display event.
/// </para>
/// <para>
/// These tests cover the parts of the re-scan that have behaviour of their own.
/// The enumeration itself needs physically unplugging a monitor to exercise, and a
/// test that needs hardware either skips or lies - so what is pinned here is the
/// contract around the enumeration, which is where the regression would come back.
/// </para>
/// </summary>
public class DisplayRescanTests
{
    /// <summary>
    /// A re-scan is reachable under its own name, not only as the enumeration.
    /// <para>
    /// Asserted rather than assumed. ScanMonitors has always cleared the cache
    /// before refilling it, so this is behaviourally an alias - but the point of the
    /// name is that a caller reading <c>ScanMonitors()</c> cannot tell whether it
    /// replaces what was known or merely reports what is there. That ambiguity is
    /// exactly what let the guards in Push and Reset read as safe.
    /// </para>
    /// </summary>
    [Fact]
    public void A_rescan_is_reachable_under_its_own_name()
    {
        var display = new DisplayService();

        IReadOnlyList<string> monitors = display.Rescan();

        Assert.NotNull(monitors);
        Assert.NotEmpty(monitors);
    }

    /// <summary>
    /// The re-scan leaves something usable behind on a machine that reports no
    /// attached displays.
    /// <para>
    /// The enumeration falls back to a single \\.\DISPLAY1 rather than returning
    /// nothing, because the alternative is an empty dropdown and a push with no
    /// target at all. That fallback is load-bearing for headless and RDP sessions,
    /// where EnumDisplayDevices can legitimately come back with nothing, and it is
    /// the case most likely to be broken by a change to this path.
    /// </para>
    /// </summary>
    [Fact]
    public void A_rescan_always_leaves_at_least_one_usable_target()
    {
        var display = new DisplayService();

        display.Rescan();

        Assert.NotEmpty(display.Monitors);
        Assert.Contains(display.MonitorChoices, c => c.Device.Length == 0);
    }

    /// <summary>
    /// The monitor list handed out is a copy, not a live view.
    /// <para>
    /// Push and Reset read it from more than one thread - the dispatcher, and the
    /// pool threads behind the night schedule and the restore - so handing out the
    /// internal list would let one of them enumerate it while a re-scan clears it.
    /// That is an InvalidOperationException, and on the UI thread it arrives with
    /// nothing on screen to explain it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_monitor_list_handed_out_is_a_copy()
    {
        var display = new DisplayService();
        display.Rescan();

        IReadOnlyList<string> first = display.Monitors;
        IReadOnlyList<string> second = display.Monitors;

        Assert.NotSame(first, second);
        Assert.Equal(first.Count, second.Count);
    }

    /// <summary>
    /// The monitor choices always lead with "All screens", and it is never
    /// duplicated by a real entry.
    /// <para>
    /// An empty device name means every screen, so a second entry with an empty
    /// name would be a second, indistinguishable "All screens" in every slot
    /// dropdown. The entry is built rather than taken from the enumeration, which
    /// is what makes that safe, and it is what the refresh button and the re-scan
    /// both rebuild.
    /// </para>
    /// </summary>
    [Fact]
    public void The_monitor_choices_lead_with_all_screens_and_only_once()
    {
        var display = new DisplayService();

        display.Rescan();

        List<MonitorChoice> choices = new(display.MonitorChoices);

        Assert.NotEmpty(choices);
        Assert.Equal(string.Empty, choices[0].Device);
        Assert.Single(choices, c => c.Device.Length == 0);
    }

    /// <summary>
    /// A target that has gone away must not stop the push dead.
    /// <para>
    /// This is the dock case. Push refuses when its target names a screen it cannot
    /// find, and reports NO SUCH SCREEN - right for a mistyped choice, wrong for a
    /// cable that moved. Undocking renumbers \\.\DISPLAY2 away, the preset scoped
    /// to it now matches nothing, and every subsequent re-push refuses, so the
    /// screen sits on whatever Windows put there while the app believes it is
    /// defending a ramp.
    /// </para>
    /// <para>
    /// A re-scan that cannot find the target widens it to all screens instead. The
    /// curve is still the one the user chose; losing it because a display was
    /// renumbered is the worse of the two failures. This asserts the reconciliation
    /// rather than the Push that depends on it: Push needs a real monitor.
    /// </para>
    /// </summary>
    [Fact]
    public void A_target_naming_a_device_that_does_not_exist_is_widened_to_all_screens()
    {
        var display = new DisplayService();
        display.Rescan();

        // Scoped to a device that cannot exist on any machine, then re-scanned.
        // The reconciliation happens inside the enumeration, which is what a
        // WM_DISPLAYCHANGE triggers.
        display.ScopeToForTest(@"\\.\DISPLAY97");
        display.Rescan();

        Assert.Equal(string.Empty, display.ActiveDeviceForTest);
    }

    /// <summary>
    /// A target that is still present is left alone, including one matched only by
    /// the suffix rule Push itself uses.
    /// <para>
    /// The suffix case matters because Push accepts it: \\.\DISPLAY2 matches
    /// DISPLAY2. Reconciling on exact equality alone would therefore break a
    /// target Push is perfectly happy with, which is the regression this guards.
    /// </para>
    /// </summary>
    [Fact]
    public void A_target_that_is_still_attached_is_not_widened()
    {
        var display = new DisplayService();
        display.Rescan();

        string present = display.Monitors[0];
        display.ScopeToForTest(present);
        display.Rescan();

        Assert.Equal(present, display.ActiveDeviceForTest);

        // And by suffix, which is the rule Push applies.
        display.ScopeToForTest(present.Replace(@"\\.\", string.Empty, System.StringComparison.Ordinal));
        display.Rescan();

        Assert.NotEqual(string.Empty, display.ActiveDeviceForTest);
    }

    /// <summary>
    /// Re-scanning does not disturb the remembered ramps.
    /// <para>
    /// Those are what the exit path puts back, and they are keyed by device name -
    /// \\.\DISPLAY1 does not change identity when a second screen appears, it is
    /// the same adapter. A re-scan that cleared them would lose the ability to
    /// restore a screen that has just been unplugged and re-plugged, which is
    /// exactly when someone reaches for the app.
    /// </para>
    /// </summary>
    [Fact]
    public void Rescanning_leaves_the_remembered_ramps_alone()
    {
        var display = new DisplayService();

        display.Rescan();
        int afterFirstScan = display.RememberedRampCountForTest;

        display.Rescan();
        display.Rescan();

        Assert.Equal(afterFirstScan, display.RememberedRampCountForTest);
    }

    /// <summary>
    /// The working preset survives a re-scan.
    /// <para>
    /// It is what Push builds the ramp from, so losing it to a re-scan would leave
    /// the service defending nothing while still reporting a preset in hand. Cheap
    /// to check and it is the field the gamma lock's whole re-push path reads.
    /// </para>
    /// </summary>
    [Fact]
    public void Rescanning_leaves_the_working_preset_alone()
    {
        var display = new DisplayService();
        display.Rescan();

        display.SetWorking(new DisplayPreset { Id = "night", Name = "Night", Gamma = 1.4 });

        display.Rescan();

        Assert.Equal("night", display.Working.Id);
        Assert.Equal(1.4, display.Working.Gamma, 3);
    }
}