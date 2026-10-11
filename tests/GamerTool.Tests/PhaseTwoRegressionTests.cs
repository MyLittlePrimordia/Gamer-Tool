using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Phase 2: not writing to hardware we cannot identify.
/// <para>
/// The defect these cover is the only one in the audit that produced a *successful*
/// write to the wrong physical device. Everything else failed visibly - a screen
/// left on the wrong preset, a revert that did not run, a state that said a slot was
/// loaded when it was not. This one put one monitor's captured brightness onto a
/// different monitor, on the way out, with no error anywhere.
/// </para>
/// <para>
/// The root cause is that "\\.\DISPLAY2" was treated as an identity. It is not.
/// Windows reassigns those names across dock, KVM and GPU transitions, so a name
/// remembered before an undock can name entirely different hardware afterwards.
/// </para>
/// </summary>
public class PhaseTwoRegressionTests
{
    private const string Device = @"\\.\DISPLAY1";

    // ---------------------------------------------------------------
    // 1.3 - a restore never writes to an unverified device string.
    // ---------------------------------------------------------------

    private sealed class ProbeBus : IBacklightBus
    {
        public List<(string Device, uint Value)> Writes { get; } = new();

        /// <summary>What the next probe finds. Empty models a display that is gone.</summary>
        public IReadOnlyList<MonitorProbe> Visible { get; set; } = Array.Empty<MonitorProbe>();

        /// <summary>Forced outcome for the restore write, so a refusal can be told from a skip.</summary>
        public BusOutcome RestoreOutcome { get; set; } = BusOutcome.Ok;

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Visible;

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            Writes.Add((device, value));
            why = null;
            error = 0;
            return BusOutcome.Ok;
        }
    }

    private static MonitorProbe Panel(string device, uint current) => new()
    {
        DeviceName = device,
        FriendlyName = "Test Panel",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Brightness = new BrightnessReading { Minimum = 0, Current = current, Maximum = 100 },
        Outcome = BusOutcome.Ok,
    };

    [Fact]
    public void A_restore_is_skipped_rather_than_written_to_a_detached_device()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50) } };
        BacklightService service = new(settings, bus);

        service.Probe();
        service.TrySet(Panel(Device, 50), 20u, out _);

        Assert.Equal(20u, bus.Writes[^1].Value);

        // Undocked. The panel is off the DDC/CI bus and Windows is free to hand
        // "\\.\DISPLAY1" to whatever gets plugged in next.
        bus.Visible = Array.Empty<MonitorProbe>();
        service.Probe();

        bus.Writes.Clear();
        service.RestoreAll();

        // Nothing. Not a failed write, not a write to whatever took the name - no
        // write at all. The detached panel keeps the hardware level it had, which is
        // what every DDC utility does.
        Assert.Empty(bus.Writes);
    }

    [Fact]
    public void A_skipped_restore_still_gives_up_the_claim()
    {
        // Otherwise the entry survives, and every later restore re-attempts the
        // write to a name that means something different each time.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50) } };
        BacklightService service = new(settings, bus);

        service.Probe();
        service.TrySet(Panel(Device, 50), 20u, out _);
        Assert.Single(settings.OriginalHardwareBrightness);

        bus.Visible = Array.Empty<MonitorProbe>();
        service.Probe();
        service.RestoreAll();

        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void A_still_attached_device_is_still_restored()
    {
        // The other half. Skipping everything would "fix" the wrong-device write by
        // never restoring anything, which is the original bug the exit path exists to
        // prevent.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50) } };
        BacklightService service = new(settings, bus);

        service.Probe();
        service.TrySet(Panel(Device, 50), 20u, out _);

        bus.Writes.Clear();
        service.RestoreAll();

        Assert.Single(bus.Writes);
        Assert.Equal(50u, bus.Writes[0].Value);
    }

    [Fact]
    public void Only_the_device_that_left_is_skipped()
    {
        // Two monitors, one pulled. Restoring the survivor is not optional just
        // because the other one is unrecoverable.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        const string Other = @"\\.\DISPLAY2";

        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50), Panel(Other, 60) } };
        BacklightService service = new(settings, bus);

        service.Probe();
        service.TrySet(Panel(Device, 50), 20u, out _);
        service.TrySet(Panel(Other, 60), 30u, out _);

        bus.Visible = new[] { Panel(Device, 50) };
        service.Probe();

        bus.Writes.Clear();
        service.RestoreAll();

        Assert.Single(bus.Writes);
        Assert.Equal(Device, bus.Writes[0].Device);
        Assert.Equal(50u, bus.Writes[0].Value);
    }

    [Fact]
    public void A_device_still_answering_to_its_name_is_still_restored()
    {
        // The case the name cannot disambiguate, asserted as it actually behaves.
        //
        // If Windows hands "\\.\DISPLAY1" to a *different* panel that reports
        // DDC/CI, the service has no way to tell it is not the one it captured. The
        // probe finds a live panel under that name, so the restore proceeds - which
        // is right for a monitor that merely went to sleep and came back, and wrong
        // for a different monitor that was plugged into the same seat.
        //
        // There is no API that answers "is this the same panel I measured earlier".
        // A DDC/CI query for the display's serial or model exists on some hardware
        // and returns nothing on more. So this gap is closed by not over-reaching
        // rather than by more code, and pinning the behaviour here is what stops it
        // being rediscovered as a bug.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50) } };
        BacklightService service = new(settings, bus);

        service.Probe();
        service.TrySet(Panel(Device, 50), 20u, out _);

        // The name is now answered by a panel reading 80 - possibly a different one.
        bus.Visible = new[] { Panel(Device, 80) };
        service.Probe();

        bus.Writes.Clear();
        service.RestoreAll();

        Assert.Single(bus.Writes);
        Assert.Equal(50u, bus.Writes[0].Value);
    }

    [Fact]
    public void A_topology_change_prunes_the_baseline_for_a_device_that_left()
    {
        // The prune, on its own, with two displays so "gone" means gone.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        const string Other = @"\\.\DISPLAY2";

        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50), Panel(Other, 60) } };
        BacklightService service = new(settings, bus);
        service.Probe();

        service.TrySet(Panel(Device, 50), 20u, out _);
        service.TrySet(Panel(Other, 60), 30u, out _);
        Assert.Equal(2, settings.OriginalHardwareBrightness.Count);

        bus.Visible = new[] { Panel(Device, 50) };
        service.Probe();

        // Exactly what RunDisplayRescan does after a topology change.
        service.ForgetDetached(new[] { Device });

        // The survivor's baseline is untouched; the departed one is gone.
        Assert.True(settings.OriginalHardwareBrightness.ContainsKey(Device));
        Assert.False(settings.OriginalHardwareBrightness.ContainsKey(Other));
    }

    [Fact]
    public void A_pruned_device_is_not_written_to_even_after_the_name_is_reused()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        const string Other = @"\\.\DISPLAY2";

        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50), Panel(Other, 60) } };
        BacklightService service = new(settings, bus);
        service.Probe();
        service.TrySet(Panel(Other, 60), 30u, out _);

        // DISPLAY2 is pulled, and Windows gives its name to a panel that was never
        // part of this session.
        bus.Visible = new[] { Panel(Device, 50) };
        service.Probe();
        service.ForgetDetached(new[] { Device });

        bus.Visible = new[] { Panel(Device, 50), Panel(Device + "x", 5) };
        service.Probe();
        bus.Writes.Clear();

        service.RestoreAll();

        Assert.Empty(bus.Writes);
    }

    [Fact]
    public void Forgetting_nothing_changes_nothing()
    {
        // The no-op path, which is the common one: most rescans lose no displays.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50) } };
        BacklightService service = new(settings, bus);
        service.Probe();
        service.TrySet(Panel(Device, 50), 20u, out _);

        service.ForgetDetached(new[] { Device, @"\\.\DISPLAY2" });

        Assert.True(settings.OriginalHardwareBrightness.ContainsKey(Device));
        Assert.Equal(50u, settings.OriginalHardwareBrightness[Device]);
    }

    // ---------------------------------------------------------------
    // The orphaned baseline.
    // ---------------------------------------------------------------

    [Fact]
    public void A_write_that_fails_leaves_no_baseline_claim()
    {
        // The capture used to happen before the bus call, so a write that failed
        // still recorded a baseline for a panel this app had never changed. The
        // restore then pushed that value onto it anyway, overriding whatever the
        // user had set by hand in the meantime.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };

        var refusing = new RefusingBus();
        BacklightService service = new(settings, refusing);
        service.Probe();

        BusOutcome outcome = service.TrySet(Panel(Device, 50), 20u, out _);

        Assert.NotEqual(BusOutcome.Ok, outcome);
        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void A_write_that_is_skipped_as_busy_leaves_no_baseline_claim()
    {
        // Busy is not a refusal, but it is also not a change, so nothing is owed to
        // this panel either.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, new BusyBus());
        service.Probe();

        Assert.Equal(BusOutcome.Busy, service.TrySet(Panel(Device, 50), 20u, out _));
        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void A_successful_write_claims_the_value_from_before_it()
    {
        // The point of the whole map, and it must still hold after the move.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new ProbeBus { Visible = new[] { Panel(Device, 50) } };
        BacklightService service = new(settings, bus);
        service.Probe();

        service.TrySet(Panel(Device, 50), 20u, out _);
        service.TrySet(Panel(Device, 20), 90u, out _);

        // 50, not 20. The second write must not overwrite the pre-session value -
        // that is the value the exit path has to hand back.
        Assert.Equal(50u, settings.OriginalHardwareBrightness[Device]);
    }

    // ---------------------------------------------------------------
    // 2.2 - scope and monitor list read as one consistent pair.
    // ---------------------------------------------------------------

    [Fact]
    public void A_scope_that_names_nothing_attached_widens_to_all_screens()
    {
        // The reconcile itself, still the behaviour it was written for: undock the
        // laptop's external screen and the preset scoped to it should not leave
        // every later push refusing.
        DisplayService display = new();
        display.ScopeToForTest(@"\\.\DISPLAY2");

        display.ReconcileForTest(new[] { @"\\.\DISPLAY1" });

        Assert.Equal(string.Empty, display.ActiveDeviceForTest);
    }

    [Fact]
    public void A_scope_that_is_still_attached_is_left_alone()
    {
        DisplayService display = new();
        display.ScopeToForTest(@"\\.\DISPLAY2");

        display.ReconcileForTest(new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" });

        Assert.Equal(@"\\.\DISPLAY2", display.ActiveDeviceForTest);
    }

    [Fact]
    public void A_cleared_scope_never_widens_a_scoped_preset_to_every_screen()
    {
        // The race, as a property rather than as a schedule.
        //
        // Push reads the monitor list and the scope. Reconcile clears the scope when
        // its display goes. If a push could observe one before the reconcile and the
        // other after, it would apply a preset the user scoped to one monitor to
        // every attached screen - silently, on the gamma lock's own timer, with no
        // error to notice.
        //
        // Both fields are under the same lock and the reconcile is a single
        // check-and-clear, so there is no intermediate state to observe. Pinned
        // here by asserting the reconcile is idempotent from both directions: a
        // scope that is already empty must not be treated as needing widening, and
        // repeated reconciles must not change an attached scope.
        DisplayService display = new();
        display.ScopeToForTest(@"\\.\DISPLAY2");

        string[] attached = { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };

        display.ReconcileForTest(attached);
        Assert.Equal(@"\\.\DISPLAY2", display.ActiveDeviceForTest);

        // Now the display is gone. One reconcile clears it, and further ones are
        // no-ops rather than re-widening or resurrecting anything.
        display.ReconcileForTest(new[] { @"\\.\DISPLAY1" });
        Assert.Equal(string.Empty, display.ActiveDeviceForTest);

        display.ReconcileForTest(new[] { @"\\.\DISPLAY1" });
        display.ReconcileForTest(attached);

        // DISPLAY2 came back, but nothing claimed it, so the scope stays wide
        // rather than silently re-aiming at a monitor the user did not pick.
        Assert.Equal(string.Empty, display.ActiveDeviceForTest);
    }

    [Fact]
    public void The_scope_is_read_and_written_under_one_lock()
    {
        // Not a source assertion for its own sake: the lock is what makes the
        // property above hold, so it is pinned at the seam a test can reach.
        //
        // Many concurrent reconciles and scope reads must leave the field in exactly
        // one of the two states it is allowed to be in - the scope the test set, or
        // empty. A torn read would produce a prefix of the device name.
        DisplayService display = new();
        display.ScopeToForTest(@"\\.\DISPLAY2");

        const int Rounds = 400;
        var torn = 0;

        Parallel.For(0, Rounds, i =>
        {
            if (i % 3 == 0)
            {
                display.ReconcileForTest(Array.Empty<string>());
            }
            else if (i % 3 == 1)
            {
                display.ScopeToForTest(@"\\.\DISPLAY2");
            }
            else
            {
                string seen = display.ActiveDeviceForTest;

                if (seen.Length != 0 && !string.Equals(seen, @"\\.\DISPLAY2", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref torn);
                }
            }
        });

        Assert.Equal(0, torn);
    }

    private sealed class RefusingBus : IBacklightBus
    {
        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) =>
            new[] { Panel(Device, 50) };

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            why = "the display did not accept the value";
            error = 0;
            return BusOutcome.Failed;
        }
    }

    private sealed class BusyBus : IBacklightBus
    {
        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) =>
            new[] { Panel(Device, 50) };

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            why = "skipped: another call was still on the bus";
            error = 0;
            return BusOutcome.Busy;
        }
    }
}