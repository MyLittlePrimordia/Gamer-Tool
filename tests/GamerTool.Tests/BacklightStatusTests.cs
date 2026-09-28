using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The copy under a monitor's name on the Display tab.
/// <para>
/// The thing these tests exist to prevent is the sentence this replaced. "This
/// monitor doesn't support hardware brightness" was shown to users whose monitor
/// supported it perfectly well, on hardware where a 240Hz display had answered a
/// brightness read minutes earlier and then stopped being reachable because the
/// graphics driver was not providing a link. Blaming the panel sends people
/// looking for a monitor fault that is not there.
/// </para>
/// </summary>
public class BacklightStatusTests
{
    private const string Device = @"\\.\DISPLAY1";

    private static MonitorProbe Probe(
        BusOutcome outcome = BusOutcome.NoDdcPathway,
        string? blocked = null,
        TargetHealth target = TargetHealth.Complete) => new()
    {
        DeviceName = Device,
        FriendlyName = "Test Monitor",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Outcome = outcome,
        BlockedReason = blocked,
        Target = new TargetHealthReading(target, Array.Empty<string>())
    };

    private static MonitorProbe Live() => new()
    {
        DeviceName = Device,
        FriendlyName = "Test Monitor",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Outcome = BusOutcome.Ok,
        Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 }
    };

    [Fact]
    public void A_working_display_describes_what_the_slider_does()
    {
        Assert.Contains("backlight", BacklightStatus.For(Live()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_driver_link_does_not_blame_the_monitor()
    {
        // The sentence that has to stop appearing. The panel is not the problem:
        // the driver listed it and then declined to give it a DDC/CI handle.
        string shown = BacklightStatus.For(Probe(BusOutcome.NoDdcPathway));

        Assert.DoesNotContain("monitor doesn't support", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not support", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("driver", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_a_monitor_that_answered_and_declined_is_told_it_did_not_respond()
    {
        string shown = BacklightStatus.For(Probe(BusOutcome.Refused));

        Assert.Contains("didn't respond", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_display_that_was_never_asked_is_not_told_it_failed()
    {
        // Nothing was concluded, so nothing may be claimed. A row saying "not
        // supported" here is a guess, and the row is what the user acts on.
        string busy = BacklightStatus.For(Probe(BusOutcome.Busy));
        string timedOut = BacklightStatus.For(Probe(BusOutcome.TimedOut));

        Assert.DoesNotContain("not supported", busy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not supported", timedOut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("doesn't support", busy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("doesn't support", timedOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stub_target_is_named_as_the_driver_rather_than_the_hardware()
    {
        // The real machine, and the one case where the driver has demonstrably
        // not set the display up at all.
        string shown = BacklightStatus.For(Probe(BusOutcome.NoDdcPathway, target: TargetHealth.Stub));

        Assert.Contains("driver", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("monitor", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_excluded_display_points_at_the_switch()
    {
        // The one case where the user really can do something about it, so it is
        // the one case that gets an instruction.
        string shown = BacklightStatus.For(Probe(BusOutcome.Failed, "on your exclusion list"));

        Assert.Contains("off and on", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edid_the_app_refused_to_trust_says_so_without_guessing_why()
    {
        string shown = BacklightStatus.For(Probe(BusOutcome.Failed, "EDID not credible: bad header"));

        Assert.Contains("left alone", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_verdict_is_long_enough_to_wrap_a_row()
    {
        // One line under a name. Anything longer turns the row into a paragraph
        // and pushes the next monitor's name off the visible area.
        foreach (BusOutcome outcome in new[]
        {
            BusOutcome.Ok, BusOutcome.Refused, BusOutcome.Busy,
            BusOutcome.TimedOut, BusOutcome.NoDdcPathway, BusOutcome.Failed
        })
        {
            string shown = BacklightStatus.For(Probe(outcome));

            Assert.True(shown.Length < 90, "too long for a row (" + shown.Length + "): " + shown);
        }
    }

    [Fact]
    public void No_status_ever_contains_a_windows_error_code()
    {
        // The technical half belongs in Copy diagnostics. A user cannot look up
        // 0xC0262581 and a row that prints it is unreadable as well as useless.
        MonitorProbe monitor = Probe(BusOutcome.Refused);
        monitor.NoReplyBecause = "\"Generic PnP Monitor\" declined 0x10 after 3 attempts, 0xC0262581 I2C_NOT_SUPPORTED";

        Assert.DoesNotContain("0xC0262581", BacklightStatus.For(monitor), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_probe_is_handled_rather_than_throwing()
    {
        Assert.Throws<ArgumentNullException>(() => BacklightStatus.For(null!));
    }

    [Fact]
    public void A_machine_with_one_working_display_keeps_the_feature()
    {
        // Three monitors, two of which cannot be reached. The user has a working
        // slider on the third, and hiding the whole feature would take away
        // something they can actually use.
        List<MonitorProbe> monitors = new()
        {
            Live(),
            Probe(),
            Probe()
        };

        Assert.False(BacklightStatus.ShouldRetireMachine(monitors, 99));
    }

    [Fact]
    public void A_machine_where_nothing_works_retires_the_feature()
    {
        List<MonitorProbe> monitors = new() { Probe(), Probe() };

        Assert.True(BacklightStatus.ShouldRetireMachine(monitors, 2));
    }

    [Fact]
    public void A_machine_with_no_displays_at_all_is_never_retired()
    {
        // Nothing was asked, so nothing was learned. Retiring here would hide the
        // feature on a laptop with the lid closed, and put it back when opened.
        Assert.False(BacklightStatus.ShouldRetireMachine(new List<MonitorProbe>(), 99));
    }
}
