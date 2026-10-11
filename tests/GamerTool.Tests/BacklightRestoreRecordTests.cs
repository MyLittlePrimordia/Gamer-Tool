using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What the remembered-brightness map is allowed to remember, and for how long.
/// <para>
/// The map records the brightness each display was at before this app first
/// touched it, so the exit path can hand it back. It only ever described the run
/// that was in progress, but it was written into settings.json like everything
/// else, and TrySet reads it back as "already captured, do not capture again".
/// So an entry left over from an earlier launch suppressed the capture in the
/// next one, and a restore several sessions later dragged a monitor back to a
/// brightness that had stopped being relevant the moment that session closed.
/// </para>
/// <para>
/// Nothing ever read the map back to recover from a crash - there is no startup
/// restore - so an entry that survived a launch carried no information this
/// session could use. It started each run empty instead.
/// </para>
/// <para>
/// Every test here runs against a fake bus and touches no real monitor.
/// </para>
/// </summary>
public class BacklightRestoreRecordTests
{
    private const string Device = @"\\.\DISPLAY1";

    private sealed class FakeBus : IBacklightBus
    {
        public List<uint> Writes { get; } = new();

        /// <summary>Whether the probe still finds the display. Turned off to model a dock being pulled.</summary>
        public bool Visible { get; set; } = true;

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys)
        {
            if (!Visible)
            {
                return Array.Empty<MonitorProbe>();
            }

            return new[]
            {
                new MonitorProbe
                {
                    DeviceName = Device,
                    FriendlyName = "Test Monitor",
                    Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
                    Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
                    Outcome = BusOutcome.Ok
                }
            };
        }

        public BusOutcome TrySetBrightness(
            string deviceName,
            uint value,
            uint minimum,
            uint maximum,
            out string? why,
            out int win32Error)
        {
            Writes.Add(value);
            why = null;
            win32Error = 0;
            return BusOutcome.Ok;
        }
    }

    private static MonitorProbe LiveProbe() => new()
    {
        DeviceName = Device,
        FriendlyName = "Test Monitor",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
        Outcome = BusOutcome.Ok
    };

    [Fact]
    public void A_record_left_by_a_previous_session_does_not_survive_the_start_of_this_one()
    {
        AppSettings settings = new()
        {
            HardwareBrightnessEnabled = true,
            OriginalHardwareBrightness = { [Device] = 20 }
        };

        _ = new BacklightService(settings, new FakeBus());

        // Whatever an earlier run was holding, this run has not touched a display
        // yet, so it has nothing to put back and nothing to stand on.
        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void The_first_write_of_a_session_captures_the_value_actually_on_screen()
    {
        AppSettings settings = new()
        {
            HardwareBrightnessEnabled = true,
            OriginalHardwareBrightness = { [Device] = 20 }
        };

        var bus = new FakeBus();
        BacklightService service = new(settings, bus);

        MonitorProbe monitor = LiveProbe();
        Assert.Equal(BusOutcome.Ok, service.TrySet(monitor, 70u, out _));

        // 50 is what the probe read, which is what the display is at now. The 20
        // from the earlier session is not what is on screen and must not be what
        // the exit path tries to restore.
        Assert.Equal(50u, settings.OriginalHardwareBrightness[Device]);
    }

    [Fact]
    public void Restoring_puts_the_captured_value_back_and_empties_the_record()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new FakeBus();
        BacklightService service = new(settings, bus);

        // Probed, because a restore only writes to a device the service has
        // verified is attached. Restore used to fall back to an assumed maximum and
        // write to the device string regardless, which on a reassigned
        // \\.\DISPLAYn meant putting one monitor's baseline onto a different panel.
        service.Probe();

        MonitorProbe monitor = LiveProbe();
        service.TrySet(monitor, 70u, out _);
        Assert.Equal(70u, bus.Writes[^1]);

        service.RestoreAll();

        // Back to where the display started, and nothing left outstanding.
        Assert.Equal(50u, bus.Writes[^1]);
        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void Restoring_skips_a_device_that_is_no_longer_attached()
    {
        // The wrong-device write, and the reason the skip above exists.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new FakeBus();
        BacklightService service = new(settings, bus);
        service.Probe();

        MonitorProbe monitor = LiveProbe();
        service.TrySet(monitor, 70u, out _);
        Assert.Single(settings.OriginalHardwareBrightness);

        // The dock is pulled. The next probe sees a different machine entirely, and
        // Windows may well have handed "\\.\DISPLAY1" to whatever is plugged in now.
        bus.Visible = false;
        service.Probe();

        int writesBefore = bus.Writes.Count;
        service.RestoreAll();

        // No write at all. The detached panel keeps the hardware level it had, which
        // is what every DDC utility does, and the panel that is currently attached
        // is not handed a brightness that was captured for different hardware.
        Assert.Equal(writesBefore, bus.Writes.Count);

        // And the claim is gone rather than retried forever.
        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void A_second_session_on_the_same_profile_starts_from_the_screen_not_the_last_one()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };

        // Session one: darken to 10, then put it back.
        var firstBus = new FakeBus();
        BacklightService first = new(settings, firstBus);
        first.Probe();
        first.TrySet(LiveProbe(), 10u, out _);
        first.RestoreAll();

        // Between the two sessions the user changed the brightness themselves.
        MonitorProbe nowOnScreen = LiveProbe();
        nowOnScreen.Brightness = new BrightnessReading { Minimum = 0, Current = 80, Maximum = 100 };

        // Session two on the same settings object, which is what a relaunch is.
        var secondBus = new FakeBus();
        BacklightService second = new(settings, secondBus);
        second.Probe();
        second.TrySet(nowOnScreen, 30u, out _);

        Assert.Equal(80u, settings.OriginalHardwareBrightness[Device]);

        second.RestoreAll();

        // 80, not the 50 the first session found and not the 10 it set. The value
        // handed back is the one that was on the screen when this session started.
        Assert.Equal(80u, secondBus.Writes[^1]);
    }

    [Fact]
    public void Restoring_twice_is_safe_and_does_not_write_again()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new FakeBus();
        BacklightService service = new(settings, bus);

        service.TrySet(LiveProbe(), 70u, out _);
        service.RestoreAll();
        int after = bus.Writes.Count;

        service.RestoreAll();

        // The exit path can be reached more than once, and the second pass has
        // nothing recorded to act on. A second write here would move the display
        // again on the way out.
        Assert.Equal(after, bus.Writes.Count);
    }
}
