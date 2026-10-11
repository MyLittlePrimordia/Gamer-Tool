using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Re-sending a brightness value that was skipped because the bus was busy.
/// <para>
/// The failure this exists to prevent is silent and it is worst exactly where it
/// is most likely: with three monitors, a write to one can collide with a
/// transaction on another. The value was dropped, the row still showed the
/// brightness the slider had been moved to, and the panel stayed where it was.
/// Nothing errored, so there was nothing to notice.
/// </para>
/// <para>
/// The retry has to be bounded. A bus wedged by a monitor that has stopped
/// answering must not be chased forever, so the budget is one re-send and then
/// the caller reports it.
/// </para>
/// </summary>
public class BacklightWriteRetryTests
{
    private const string Device = @"\\.\DISPLAY1";

    [Fact]
    public void A_skip_is_reported_as_busy_rather_than_as_a_failure()
    {
        // The caller cannot re-send unless it is told the difference between
        // "asked and refused" and "never asked".
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var service = new BacklightService(settings, new AlwaysBusyBus());

        MonitorProbe monitor = LiveMonitor();

        Assert.Equal(BusOutcome.Busy, service.TrySet(monitor, 40, out _));
    }

    [Fact]
    public void The_first_skip_is_allowed_a_re_send()
    {
        var retry = new BacklightWriteRetry();

        Assert.True(retry.ShouldRetry(Device));
    }

    [Fact]
    public void The_second_skip_is_not()
    {
        var retry = new BacklightWriteRetry();

        Assert.True(retry.ShouldRetry(Device));
        Assert.False(retry.ShouldRetry(Device));
    }

    [Fact]
    public void The_budget_is_per_display()
    {
        // One busy monitor must not spend another busy monitor's re-send.
        var retry = new BacklightWriteRetry();

        Assert.True(retry.ShouldRetry(@"\\.\DISPLAY1"));
        Assert.True(retry.ShouldRetry(@"\\.\DISPLAY2"));
        Assert.False(retry.ShouldRetry(@"\\.\DISPLAY1"));
        Assert.False(retry.ShouldRetry(@"\\.\DISPLAY2"));
    }

    [Fact]
    public void A_success_puts_the_budget_back()
    {
        var retry = new BacklightWriteRetry();

        Assert.True(retry.ShouldRetry(Device));
        retry.Reset(Device);

        Assert.True(retry.ShouldRetry(Device));
    }

    [Fact]
    public void A_fresh_budget_is_unspent()
    {
        var retry = new BacklightWriteRetry();

        Assert.Equal(BacklightWriteRetry.MaxRetries, retry.Remaining(Device));

        retry.ShouldRetry(Device);

        Assert.Equal(BacklightWriteRetry.MaxRetries - 1, retry.Remaining(Device));
    }

    [Fact]
    public void A_skipped_value_lands_when_it_is_sent_again()
    {
        // End to end over the fake bus: skip, then land. This is the behaviour the
        // user sees, and the reason the row no longer sits at a brightness the
        // panel is not at.
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new BusyOnceBus();
        var service = new BacklightService(settings, bus);

        MonitorProbe monitor = LiveMonitor();

        Assert.Equal(BusOutcome.Busy, service.TrySet(monitor, 40, out _));
        Assert.Equal(BusOutcome.Ok, service.TrySet(monitor, 40, out _));

        // The landed value is what the row now shows.
        Assert.Equal(40u, monitor.Brightness!.Current);
    }

    [Fact]
    public void A_value_that_never_gets_the_bus_changes_nothing_and_is_not_excluded()
    {
        AppSettings settings = new() { HardwareBrightnessEnabled = true };
        var bus = new AlwaysBusyBus();
        var service = new BacklightService(settings, bus);

        MonitorProbe monitor = LiveMonitor();
        var retry = new BacklightWriteRetry();

        // Two attempts, which is the initial send plus the one re-send.
        if (retry.ShouldRetry(Device))
        {
            service.TrySet(monitor, 40, out _);
        }

        if (retry.ShouldRetry(Device))
        {
            service.TrySet(monitor, 40, out _);
        }

        // Giving up is not the display's fault, so it must not be written off, and
        // the reading must survive so the row is still usable.
        Assert.Empty(settings.ExcludedDdcMonitors);
        Assert.NotNull(monitor.Brightness);
        Assert.True(monitor.CanControlBacklight);
    }

    private static MonitorProbe LiveMonitor() => new()
    {
        DeviceName = Device,
        FriendlyName = "Test Monitor",
        Edid = new EdidReading { Manufacturer = "TST", Verdict = EdidVerdict.Plausible },
        Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 },
        Outcome = BusOutcome.Ok
    };

    private sealed class AlwaysBusyBus : IBacklightBus
    {
        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Array.Empty<MonitorProbe>();

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            why = "skipped: another call was still on the bus";
            error = 0;
            return BusOutcome.Busy;
        }
    }

    private sealed class BusyOnceBus : IBacklightBus
    {
        private int _calls;

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded) => Array.Empty<MonitorProbe>();

        public BusOutcome TrySetBrightness(string device, uint value, uint min, uint max, out string? why, out int error)
        {
            if (_calls++ == 0)
            {
                why = "skipped: another call was still on the bus";
                error = 0;
                return BusOutcome.Busy;
            }

            why = null;
            error = 0;
            return BusOutcome.Ok;
        }
    }
}
