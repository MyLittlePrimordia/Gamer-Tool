using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The bookkeeping around a display that will not cooperate: counting refusals,
/// deciding to write a monitor off, and the way back.
/// <para>
/// The rule that matters is that a display is only written off for refusing. It
/// used to be written off for being busy, because a skip while another call was
/// on the bus returned the same false as a decline and the caller had no way to
/// tell them apart. Three slider steps landing inside the probe's two second
/// window were therefore enough to put a monitor on the persisted exclusion list
/// without the monitor ever being asked anything, after which the row was greyed
/// out on every launch until somebody hand edited settings.json. Two such rows
/// are in this machine's own log.
/// </para>
/// <para>
/// Every test here runs against a fake bus. None of them, and nothing else in
/// this suite, talks to a real monitor.
/// </para>
/// </summary>
public class BacklightExclusionTests
{
    private const string Device = @"\\.\DISPLAY1";

    /// <summary>A bus that answers with whatever the test tells it to.</summary>
    private sealed class FakeBus : IBacklightBus
    {
        public List<BusOutcome> Replies { get; } = new();

        public int ProbeCalls { get; private set; }

        public bool LastProbeSawExclusions { get; private set; }

        public int Writes { get; private set; }

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys)
        {
            ProbeCalls++;
            LastProbeSawExclusions = excludedDeviceKeys is { Count: > 0 };

            bool excluded = excludedDeviceKeys is not null
                && excludedDeviceKeys.Contains(Device, StringComparer.OrdinalIgnoreCase);

            return new[]
            {
                new MonitorProbe
                {
                    DeviceName = Device,
                    FriendlyName = "Test Monitor",
                    Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
                    EdidSource = "driver",

                    // Mirrors what the real probe does, so the exclusion path is
                    // exercised rather than assumed.
                    Brightness = excluded
                        ? null
                        : new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
                    BlockedReason = excluded ? "on your exclusion list" : null,
                    Outcome = excluded ? BusOutcome.Failed : BusOutcome.Ok
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
            Writes++;

            BusOutcome reply = Replies.Count > 0
                ? Replies[Math.Min(Writes - 1, Replies.Count - 1)]
                : BusOutcome.Ok;

            why = reply == BusOutcome.Ok ? null : "the monitor refused the brightness value";
            win32Error = reply == BusOutcome.Ok ? 0 : unchecked((int)0xC0262583u);
            return reply;
        }
    }

    private static (BacklightService Service, FakeBus Bus, AppSettings Settings) NewService()
    {
        // Opted in, because Probe() refuses to touch the bus otherwise and a test
        // that quietly skipped its own subject would be worse than no test.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new FakeBus();
        return (new BacklightService(settings, bus), bus, settings);
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
    public void A_busy_skip_is_not_a_refusal_and_does_not_exclude()
    {
        (BacklightService service, FakeBus bus, AppSettings settings) = NewService();
        bus.Replies.AddRange(new[] { BusOutcome.Busy, BusOutcome.Busy, BusOutcome.Busy, BusOutcome.Busy });

        MonitorProbe monitor = LiveProbe();
        for (int i = 0; i < 10; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        // Ten skips, and the display is still a candidate. This is the regression
        // that greyed the row out on hardware which had never been asked anything.
        Assert.Empty(settings.ExcludedDdcMonitors);
        Assert.Empty(service.Refusals);
    }

    [Fact]
    public void A_busy_skip_leaves_the_row_live()
    {
        (BacklightService service, FakeBus bus, _) = NewService();
        bus.Replies.Add(BusOutcome.Busy);

        MonitorProbe monitor = LiveProbe();
        Assert.Equal(BusOutcome.Busy, service.TrySet(monitor, 40, out _));

        // A skip must not clear the reading, or the slider greys out mid drag for
        // a monitor that is perfectly reachable a moment later.
        Assert.NotNull(monitor.Brightness);
        Assert.True(monitor.CanControlBacklight);
    }

    [Fact]
    public void A_run_of_real_refusals_does_exclude()
    {
        (BacklightService service, FakeBus bus, AppSettings settings) = NewService();
        bus.Replies.Add(BusOutcome.Refused);

        MonitorProbe monitor = LiveProbe();
        for (int i = 0; i < 3; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        Assert.Contains(Device, settings.ExcludedDdcMonitors);
    }

    [Fact]
    public void A_no_pathway_answer_also_counts_as_a_refusal()
    {
        (BacklightService service, FakeBus bus, AppSettings settings) = NewService();
        bus.Replies.Add(BusOutcome.NoDdcPathway);

        MonitorProbe monitor = LiveProbe();
        for (int i = 0; i < 3; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        Assert.Contains(Device, settings.ExcludedDdcMonitors);
    }

    [Fact]
    public void Two_refusals_are_not_enough_to_exclude()
    {
        (BacklightService service, FakeBus bus, AppSettings settings) = NewService();
        bus.Replies.Add(BusOutcome.Refused);

        MonitorProbe monitor = LiveProbe();
        for (int i = 0; i < 2; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        Assert.Empty(settings.ExcludedDdcMonitors);
        Assert.Equal(2, service.Refusals[Device]);
    }

    [Fact]
    public void A_successful_write_clears_the_refusal_count()
    {
        (BacklightService service, FakeBus bus, _) = NewService();
        bus.Replies.AddRange(new[] { BusOutcome.Refused, BusOutcome.Ok });

        MonitorProbe monitor = LiveProbe();
        service.TrySet(monitor, 40, out _);
        service.TrySet(monitor, 60, out _);

        Assert.Empty(service.Refusals);
    }

    [Fact]
    public void Forgetting_exclusions_clears_the_list_and_the_counts()
    {
        (BacklightService service, FakeBus bus, AppSettings settings) = NewService();
        bus.Replies.Add(BusOutcome.Refused);

        MonitorProbe monitor = LiveProbe();
        for (int i = 0; i < 3; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        Assert.NotEmpty(settings.ExcludedDdcMonitors);

        service.ForgetExclusions();

        Assert.Empty(settings.ExcludedDdcMonitors);
        Assert.Empty(service.Refusals);
    }

    [Fact]
    public void Forgetting_exclusions_allows_a_fresh_probe()
    {
        // The second half of the trap. The exclusion list was clearable in
        // principle but the probe guard stayed set, so switching the feature off
        // and on again re-probed nothing and the row stayed grey.
        (BacklightService service, FakeBus bus, _) = NewService();

        service.Probe();
        Assert.True(service.HasProbed);
        Assert.Equal(1, bus.ProbeCalls);

        service.ForgetExclusions();

        Assert.False(service.HasProbed);
    }

    [Fact]
    public void Forgetting_exclusions_is_committed_to_the_profile()
    {
        (BacklightService service, FakeBus bus, AppSettings settings) = NewService();
        bus.Replies.Add(BusOutcome.Refused);

        MonitorProbe monitor = LiveProbe();
        for (int i = 0; i < 3; i++)
        {
            service.TrySet(monitor, 40, out _);
        }

        service.ConsumeSettingsChanged();
        Assert.False(service.ConsumeSettingsChanged());

        service.ForgetExclusions();

        // Otherwise the cleared list is only in memory and the next launch reads
        // the old one back off disk.
        Assert.True(service.ConsumeSettingsChanged());
    }

    [Fact]
    public void An_excluded_display_is_not_probed_and_says_why()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        settings.ExcludedDdcMonitors.Add(Device);

        var bus = new FakeBus();
        var service = new BacklightService(settings, bus);

        service.Probe();

        MonitorProbe? found = service.Find(Device);
        Assert.NotNull(found);
        Assert.False(found!.CanControlBacklight);
        Assert.Equal("excluded", BacklightService.StageOf(found));
    }

    [Fact]
    public void The_row_shows_a_plain_sentence_and_not_a_refusal_string()
    {
        (BacklightService service, FakeBus bus, _) = NewService();
        bus.Replies.Add(BusOutcome.Refused);

        MonitorProbe monitor = LiveProbe();
        monitor.Brightness = null;
        monitor.NoReplyBecause = "\"Generic PnP Monitor\" declined 0x10 after 3 attempts, 0xC0262581 I2C_NOT_SUPPORTED";

        string shown = service.CapabilityOf(monitor);

        // The row used to print the whole refusal into a tooltip, which is
        // unreadable and tells a user nothing they can act on.
        Assert.Equal("This monitor doesn't support hardware brightness.", shown);
        Assert.DoesNotContain("0xC0262581", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_excluded_row_points_at_the_switch_rather_than_at_a_bus_error()
    {
        (BacklightService service, _, _) = NewService();

        string shown = service.CapabilityOf(new MonitorProbe
        {
            DeviceName = Device,
            FriendlyName = "Test Monitor",
            BlockedReason = "on your exclusion list"
        });

        // An exclusion is something the user can undo, so it is the one case
        // where the row has something useful to say.
        Assert.Contains("off and on", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_supported_display_still_says_so()
    {
        (BacklightService service, _, _) = NewService();

        Assert.Equal("Supported", service.CapabilityOf(LiveProbe()));
    }

    [Fact]
    public async Task A_second_probe_does_not_start_while_one_is_running()
    {
        // Two call sites start a probe, and the old guard was only set once a
        // probe had finished, so they could clear and repopulate the same table
        // underneath each other.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new FakeBus();
        var service = new BacklightService(settings, bus);

        System.Threading.Tasks.Task first = System.Threading.Tasks.Task.Run(() => service.Probe());
        System.Threading.Tasks.Task second = System.Threading.Tasks.Task.Run(() => service.Probe());

        await System.Threading.Tasks.Task.WhenAll(first, second);

        // One or two depending on how they interleaved, but never a table left
        // half filled: every completed probe publishes its whole result set.
        Assert.True(service.HasProbed);
        Assert.NotNull(service.Find(Device));
    }
}
