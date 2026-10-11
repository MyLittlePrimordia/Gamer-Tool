using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A laptop's own screen, reached over root\wmi, presented as one bus alongside
/// the external monitors.
/// <para>
/// The external route goes over the display cable to a monitor and a laptop's
/// internal panel has no monitor behind it to answer, which is why laptops were
/// out of scope until now. The panel does have a documented interface of its
/// own, and this is the routing between the two.
/// </para>
/// <para>
/// The rule that matters most is that the panel is additive. A machine with no
/// panel must behave exactly as it did before, which is what
/// <see cref="With_no_panel_the_external_monitors_are_untouched"/> pins down -
/// and these run on a desktop, so the panel-present paths are the ones being
/// stood in for. What cannot be stood in for is a real panel.
/// </para>
/// </summary>
public class PanelBusTests
{
    private const string ExternalOne = @"\\.\DISPLAY1";

    private sealed class FakeExternal : IBacklightBus
    {
        public List<string> Writes { get; } = new();

        public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excluded)
        {
            return new[]
            {
                new MonitorProbe
                {
                    DeviceName = ExternalOne,
                    FriendlyName = "External 1",
                    Outcome = BusOutcome.Ok,
                    Brightness = new BrightnessReading { Minimum = 0, Current = 40, Maximum = 100 },
                }
            };
        }

        public BusOutcome TrySetBrightness(string deviceName, uint value, uint min, uint max, out string? why, out int win32Error)
        {
            Writes.Add(deviceName + "=" + value);
            why = null;
            win32Error = 0;
            return BusOutcome.Ok;
        }
    }

    private static MonitorProbe PanelProbe()
    {
        return new MonitorProbe
        {
            DeviceName = PanelWmi.DeviceName,
            FriendlyName = PanelWmi.FriendlyName,
            Outcome = BusOutcome.Ok,
            Brightness = new BrightnessReading { Minimum = 0, Current = 55, Maximum = 100 },
        };
    }

    private static CompositeBacklightBus WithPanel(FakeExternal external, Func<int, bool>? set = null)
    {
        return new CompositeBacklightBus(external, () => true, PanelProbe, set ?? (_ => true));
    }

    private static CompositeBacklightBus WithoutPanel(FakeExternal external)
    {
        return new CompositeBacklightBus(external, () => false, () => null, _ => false);
    }

    [Fact]
    public void With_no_panel_the_external_monitors_are_untouched()
    {
        // The behaviour every existing install has, on every desktop. If this
        // changes, the feature that has been working is what broke.
        FakeExternal external = new();
        IReadOnlyList<MonitorProbe> probed = WithoutPanel(external).ProbeAll(null);

        MonitorProbe only = Assert.Single(probed);
        Assert.Equal(ExternalOne, only.DeviceName);
        Assert.True(only.CanControlBacklight);
        Assert.DoesNotContain(probed, p => p.DeviceName == PanelWmi.DeviceName);
    }

    [Fact]
    public void The_panel_appears_alongside_the_external_monitors()
    {
        FakeExternal external = new();
        IReadOnlyList<MonitorProbe> probed = WithPanel(external).ProbeAll(null);

        Assert.Equal(2, probed.Count);
        Assert.Contains(probed, p => p.DeviceName == ExternalOne);
        MonitorProbe panel = Assert.Single(probed, p => p.DeviceName == PanelWmi.DeviceName);
        Assert.True(panel.CanControlBacklight);
        Assert.Equal(55u, panel.Brightness!.Current);
    }

    [Fact]
    public void The_panel_answers_in_the_range_the_engine_takes()
    {
        // WmiSetBrightness takes a percentage, and no minimum or maximum is
        // published for it, so 0 to 100 is the honest answer. Anything else would
        // let a drag ask for a brightness the panel cannot reach.
        MonitorProbe panel = PanelProbe();

        Assert.Equal(0u, panel.Brightness!.Minimum);
        Assert.Equal(100u, panel.Brightness.Maximum);
    }

    [Fact]
    public void An_external_write_is_never_routed_to_the_panel()
    {
        FakeExternal external = new();
        List<int> panelWrites = new();
        CompositeBacklightBus bus = WithPanel(external, p => { panelWrites.Add(p); return true; });

        BusOutcome outcome = bus.TrySetBrightness(ExternalOne, 25, 0, 100, out _, out _);

        Assert.Equal(BusOutcome.Ok, outcome);
        Assert.Equal(new[] { ExternalOne + "=25" }, external.Writes);
        Assert.Empty(panelWrites);
    }

    [Fact]
    public void A_panel_write_is_never_routed_over_ddc()
    {
        FakeExternal external = new();
        List<int> panelWrites = new();
        CompositeBacklightBus bus = WithPanel(external, p => { panelWrites.Add(p); return true; });

        BusOutcome outcome = bus.TrySetBrightness(PanelWmi.DeviceName, 30, 0, 100, out _, out _);

        Assert.Equal(BusOutcome.Ok, outcome);
        Assert.Equal(new[] { 30 }, panelWrites);
        Assert.Empty(external.Writes);
    }

    [Fact]
    public void A_panel_that_will_not_take_the_value_is_refused_rather_than_failed()
    {
        // Refused is the answer that leads to the panel being written off after
        // the usual number of attempts. Failed would be read as the bus being
        // broken, and the panel would go quiet with nothing to show why.
        FakeExternal external = new();
        CompositeBacklightBus bus = WithPanel(external, _ => false);

        BusOutcome outcome = bus.TrySetBrightness(PanelWmi.DeviceName, 30, 0, 100, out string? why, out _);

        Assert.Equal(BusOutcome.Refused, outcome);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void A_panel_write_is_clamped_to_what_the_engine_accepts()
    {
        FakeExternal external = new();
        List<int> panelWrites = new();
        CompositeBacklightBus bus = WithPanel(external, p => { panelWrites.Add(p); return true; });

        bus.TrySetBrightness(PanelWmi.DeviceName, 500, 0, 100, out _, out _);
        bus.TrySetBrightness(PanelWmi.DeviceName, 0, 0, 100, out _, out _);

        Assert.Equal(new[] { 100, 0 }, panelWrites);
    }

    [Fact]
    public void The_panel_is_excluded_like_any_other_display()
    {
        // The switch that retires a misbehaving monitor has to retire a
        // misbehaving panel by the same path, with no special case anywhere.
        FakeExternal external = new();
        IReadOnlyList<MonitorProbe> probed = WithPanel(external).ProbeAll(new[] { PanelWmi.DeviceName });

        Assert.DoesNotContain(probed, p => p.DeviceName == PanelWmi.DeviceName);
        Assert.Contains(probed, p => p.DeviceName == ExternalOne);
    }

    [Fact]
    public void The_panel_device_name_survives_a_restart()
    {
        // WMI InstanceName is DISPLAY\<session>\<id>_0 and the id changes between
        // boots, so a name built from one would leave an orphaned exclusion
        // behind on every restart and the panel would never be retried.
        Assert.Equal("internal-panel", PanelWmi.DeviceName);
        Assert.DoesNotContain("\\", PanelWmi.DeviceName, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_panel_is_restored_on_the_way_out_like_every_other_display()
    {
        // It is a bus, so the exit path that already exists applies to it with no
        // new code: probe, write, remember what it was, put it back.
        FakeExternal external = new();
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, WithPanel(external));

        service.Probe();
        MonitorProbe panel = service.Monitors.Single(m => m.DeviceName == PanelWmi.DeviceName);

        Assert.Equal(BusOutcome.Ok, service.TrySet(panel, 20, out _));

        // What it was before the app touched it, remembered for the way out.
        Assert.Equal(55u, settings.OriginalHardwareBrightness[PanelWmi.DeviceName]);

        service.RestoreAll();
        Assert.Empty(settings.OriginalHardwareBrightness);
    }

    [Fact]
    public void A_machine_with_no_panel_probes_to_nothing_and_does_not_throw()
    {
        // Whatever WMI says on a desktop - class absent, service stopped, policy
        // off - the answer is "no panel", never an exception.
        FakeExternal external = new();
        var settings = new AppSettings { HardwareBrightnessEnabled = true };
        BacklightService service = new(settings, WithoutPanel(external));

        service.Probe();

        Assert.Single(service.Monitors);
        Assert.DoesNotContain(PanelWmi.DeviceName, settings.OriginalHardwareBrightness.Keys);
    }
}
