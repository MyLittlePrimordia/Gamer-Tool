using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// How much warning somebody gets before the app turns hardware brightness off by
/// itself.
/// <para>
/// These drive the real <see cref="BacklightService"/> against a fake bus rather
/// than calling <see cref="BacklightAvailability"/> directly, because the thing
/// that was wrong was not the policy. The policy said two rounds, and two rounds
/// is right. The bug was in the service: turning the switch back on sets
/// <c>_probed = false</c> so a probe can run, and that probe is launched by
/// <see cref="MainWindow.Setup"/> within a second of the click. It was being
/// counted like any other round, so the second half of the grace period was spent
/// before the user had closed the window and "two rounds of grace" felt like one.
/// </para>
/// <para>
/// Nothing here touches a real monitor.
/// </para>
/// </summary>
public class BacklightRetirementGraceTests
{
    private const string Device = @"\\.\DISPLAY1";

    /// <summary>A bus that reports a display which never answers, the real case here.</summary>
    private sealed class DeadBus : IBacklightBus
    {
        public bool Works { get; set; }

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys) =>
            new[] { Works ? Live() : Dead() };

        public BusOutcome TrySetBrightness(
            string deviceName,
            uint value,
            uint minimum,
            uint maximum,
            out string? why,
            out int win32Error)
        {
            why = Works ? null : "no DDC/CI pathway";
            win32Error = Works ? 0 : unchecked((int)0xC0262583u);
            return Works ? BusOutcome.Ok : BusOutcome.NoDdcPathway;
        }

        private static MonitorProbe Live() => new()
        {
            DeviceName = Device,
            FriendlyName = "Test Monitor",
            Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
            Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
            Outcome = BusOutcome.Ok
        };

        private static MonitorProbe Dead() => new()
        {
            DeviceName = Device,
            FriendlyName = "Test Monitor",
            Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
            Brightness = null,
            Outcome = BusOutcome.NoDdcPathway
        };
    }

    private static AppSettings OptedIn() => new() { HardwareBrightnessEnabled = true };

    /// <summary>
    /// A launch. The service is rebuilt against the same settings file, which is
    /// how the counter survives a restart, so a new instance is the honest way to
    /// say "the user closed and reopened the app".
    /// </summary>
    private static BacklightService Launch(AppSettings settings, DeadBus bus) =>
        new(settings, bus);

    [Fact]
    public void A_failure_streak_that_spans_two_launches_retires_the_feature()
    {
        // The baseline the grace is measured against, and the only reason the
        // policy exists at all.
        AppSettings settings = OptedIn();
        var bus = new DeadBus();

        BacklightService first = Launch(settings, bus);
        first.Probe();
        Assert.False(first.Availability.Retired);

        BacklightService second = Launch(settings, bus);
        second.Probe();

        Assert.True(second.Availability.Retired);
        Assert.True(second.ConsumeRetirement());
    }

    [Fact]
    public void The_probe_fired_by_turning_the_switch_on_does_not_count()
    {
        // The bug. Turning the switch on is what makes this probe possible, and it
        // runs immediately over the same connection that has just failed twice, so
        // counting it spends the grace period before the user has done anything.
        AppSettings settings = OptedIn();
        var bus = new DeadBus();

        BacklightService first = Launch(settings, bus);
        first.Probe();
        Launch(settings, bus).Probe();
        Assert.True(settings.HardwareBrightnessRetired);

        // The user re-enables it. Same session, so the same service: the probe the
        // switch triggers is run by the instance that was asked to re-probe, not by
        // a fresh one.
        settings.HardwareBrightnessEnabled = false;
        settings.HardwareBrightnessEnabled = true;
        first.Availability.Restore();
        first.ForgetExclusions();
        first.Probe();

        Assert.Equal(0, first.Availability.ConsecutiveRounds);
        Assert.False(
            first.Availability.Retired,
            "the probe that answers the switch the user just moved was counted as a round");
    }

    [Fact]
    public void The_free_round_still_buys_two_more_launches()
    {
        // What the user actually gets now: the switch comes back on, and it stays
        // on through two restarts before the app touches it again.
        AppSettings settings = OptedIn();
        var bus = new DeadBus();

        BacklightService session = Launch(settings, bus);
        session.Probe();
        Launch(settings, bus).Probe();

        // Asserted on the profile rather than on a service, because the second
        // launch is a different instance and the retirement is what crossed the
        // boundary. Each launch only knows its own in-memory copy.
        Assert.True(settings.HardwareBrightnessRetired);

        // The app turned the switch off, and the user puts it back.
        settings.HardwareBrightnessEnabled = true;
        session.Availability.Restore();
        session.ForgetExclusions();
        settings.HardwareBrightnessRetired = false;
        settings.HardwareBrightnessFailedRounds = 0;

        // Round zero: the probe the switch triggered. Free.
        session.Probe();
        Assert.False(session.Availability.Retired);
        Assert.Equal(0, session.Availability.ConsecutiveRounds);

        // Restart one. Counted, and one is not enough.
        BacklightService restartOne = Launch(settings, bus);
        restartOne.Probe();
        Assert.False(restartOne.Availability.Retired);
        Assert.Equal(1, restartOne.Availability.ConsecutiveRounds);

        // Restart two. Counted, and now it is over.
        BacklightService restartTwo = Launch(settings, bus);
        restartTwo.Probe();
        Assert.True(restartTwo.Availability.Retired);
    }

    [Fact]
    public void The_free_round_is_spent_only_once()
    {
        // Otherwise the exemption becomes a way to never be retired, which would
        // be worse than the original problem: a dead switch on every launch.
        AppSettings settings = OptedIn();
        var bus = new DeadBus();

        BacklightService service = Launch(settings, bus);
        service.Probe();
        service.ForgetExclusions();
        service.Probe();
        Assert.Equal(0, service.Availability.ConsecutiveRounds);

        // Second round after the exemption, on a fresh launch this time.
        BacklightService next = Launch(settings, bus);
        next.Probe();
        Assert.Equal(1, next.Availability.ConsecutiveRounds);
    }

    [Fact]
    public void A_display_that_answers_in_the_free_round_is_believed_immediately()
    {
        // The exemption is only about failing. A round that reaches a display still
        // clears the streak, so this is not a way to bank a pass and then ignore a
        // working monitor.
        AppSettings settings = OptedIn();
        var bus = new DeadBus();

        BacklightService service = Launch(settings, bus);
        service.Probe();
        Assert.Equal(1, service.Availability.ConsecutiveRounds);

        service.ForgetExclusions();
        bus.Works = true;
        service.Probe();

        Assert.Equal(0, service.Availability.ConsecutiveRounds);
        Assert.False(service.Availability.Retired);
    }

    [Fact]
    public void A_free_round_persists_a_streak_of_zero_rather_than_erasing_it()
    {
        // The switch zeroes the streak on purpose, that is the whole point of
        // re-enabling it, so the free round writing that zero back is correct. What
        // it must not do is leave the previous number on disk, because the next
        // launch would start from a streak the user had already cleared and could
        // retire sooner than the two rounds it was promised.
        AppSettings settings = OptedIn();
        var bus = new DeadBus();

        BacklightService session = Launch(settings, bus);
        session.Probe();
        Assert.Equal(1, settings.HardwareBrightnessFailedRounds);

        session.ForgetExclusions();
        session.Probe();

        Assert.Equal(0, settings.HardwareBrightnessFailedRounds);

        // And the next launch genuinely starts from nothing.
        BacklightService next = Launch(settings, bus);
        next.Probe();
        Assert.Equal(1, next.Availability.ConsecutiveRounds);
        Assert.False(next.Availability.Retired);
    }

    [Fact]
    public void The_threshold_is_still_two_counted_rounds()
    {
        Assert.Equal(2, BacklightAvailability.RoundsBeforeRetiring);
    }
}
