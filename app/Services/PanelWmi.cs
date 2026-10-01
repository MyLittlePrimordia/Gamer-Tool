using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// A laptop's own screen, over <c>root\wmi</c>.
/// <para>
/// The DDC/CI route used for external monitors goes over the display cable to a
/// monitor, and a laptop's internal panel has no monitor behind it to answer.
/// Every documented attempt to reach a panel over DDC is vendor specific and
/// unreliable, so laptops were left out of the hardware brightness feature
/// entirely - which is a large share of the people this app is for.
/// </para>
/// <para>
/// The panel does have a documented interface of its own, and it is this:
/// <c>WmiMonitorBrightnessMethods.WmiSetBrightness(uint32 Timeout, uint8
/// Brightness)</c>, with the current level readable from
/// <c>WmiMonitorBrightness.CurrentBrightness</c> and the original recoverable
/// through <c>WmiRevertToPolicyBrightness</c>. Minimum client is Windows Vista.
/// </para>
/// <para>
/// Everything here is defensive to the point of being quiet. The classes are
/// absent on a desktop, WMI can be disabled by policy, and any of it can fail for
/// reasons that are not this app's business. None of those are errors: they mean
/// there is no panel to talk to, and the caller falls back to what it already
/// has. The one thing this must never do is take the external monitors down with
/// it, which is why it is a separate bus rather than a branch inside the DDC one.
/// </para>
/// </summary>
public static class PanelWmi
{
    /// <summary>The namespace the panel's brightness classes live in.</summary>
    private const string Namespace = @"root\wmi";

    /// <summary>
    /// A short, stable name for the panel as a bus device, so it sorts and
    /// compares like anything else in the exclusion list.
    /// <para>
    /// Deliberately not derived from the WMI <c>InstanceName</c>: those embed a
    /// session specific id that changes between boots, so a name built from one
    /// would leave an orphaned exclusion behind on every restart.
    /// </para>
    /// </summary>
    public const string DeviceName = "internal-panel";

    /// <summary>What the panel is called on screen.</summary>
    public const string FriendlyName = "Laptop screen";

    /// <summary>Whether this machine has a panel that answers at all.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                Namespace,
                "SELECT InstanceName FROM WmiMonitorBrightnessMethods");
            using ManagementObjectCollection results = searcher.Get();
            return results.Count > 0;
        }
        catch (Exception ex)
        {
            // No such class on a desktop, or WMI is not running, or a policy has
            // it off. All of those mean the same thing here: no panel to talk to.
            TraceLog.Write("PANEL availability: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>The panel's brightness as a percentage, or null when it will not say.</summary>
    public static int? Read()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                Namespace,
                "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"], CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("PANEL read: " + ex.GetType().Name);
        }

        return null;
    }

    /// <summary>
    /// Asks the panel for a new brightness, as a percentage.
    /// <para>
    /// The timeout is one second and is not negotiable: this is how long the
    /// system is asked to hold before it gives up changing its own backlight, and
    /// it is passed per call rather than being the caller's problem to remember.
    /// </para>
    /// </summary>
    public static bool Set(int percent)
    {
        try
        {
            byte level = (byte)Math.Clamp(percent, 0, 100);

            using ManagementObjectSearcher searcher = new(
                Namespace,
                "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            using ManagementObjectCollection results = searcher.Get();

            bool any = false;
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    ManagementObject method = (ManagementObject)item;
                    method.InvokeMethod("WmiSetBrightness", new object[] { 1u, level });
                    any = true;
                }
            }

            return any;
        }
        catch (Exception ex)
        {
            TraceLog.Write("PANEL write: " + ex.GetType().Name);
            return false;
        }
    }


    /// <summary>
    /// The panel as a bus probe, or null when this machine does not have one that
    /// is currently answering.
    /// <para>
    /// The range is reported as a plain 0 to 100 because that is the unit
    /// <c>WmiSetBrightness</c> takes, and no minimum or maximum is published
    /// anywhere for it. Reporting the engine's own defaults instead would let a
    /// drag ask for a brightness the panel cannot reach.
    /// </para>
    /// </summary>
    public static MonitorProbe? Probe()
    {
        int? current = Read();
        if (!current.HasValue)
        {
            return null;
        }

        return new MonitorProbe
        {
            DeviceName = DeviceName,
            FriendlyName = FriendlyName,
            EdidSource = "wmi",
            Outcome = BusOutcome.Ok,
            Brightness = new BrightnessReading
            {
                Minimum = 0,
                Current = (uint)Math.Clamp(current.Value, 0, 100),
                Maximum = 100,
                CodeType = VcpCodeType.SetParameter,
            },
        };
    }
}


/// <summary>
/// The external monitors and the laptop panel, as one bus.
/// <para>
/// Two very different interfaces have to look like one to
/// <see cref="BacklightService"/>, because everything above the bus - probing,
/// the exclusion list, retiring a display that misbehaves, restoring what was
/// found, the debounce - is written against a single enumeration and would all
/// have to be duplicated otherwise.
/// </para>
/// <para>
/// Reads go to both. Writes go to whichever one owns the device asked about, so
/// the panel never receives a DDC call and an external monitor never receives a
/// WMI one. If the panel is missing, its half is simply not there: this returns
/// what DDC found, which is exactly what the app did before it knew about
/// panels.
/// </para>
/// </summary>
public sealed class CompositeBacklightBus : IBacklightBus
{
    private readonly IBacklightBus _external;

    private readonly Func<bool> _panelAvailable;
    private readonly Func<MonitorProbe?> _panelProbe;
    private readonly Func<int, bool> _panelSet;

    /// <summary>
    /// The real thing: external monitors over DDC and the laptop panel over WMI.
    /// </summary>
    public CompositeBacklightBus()
        : this(null, PanelWmi.IsAvailable, PanelWmi.Probe, PanelWmi.Set)
    {
    }

    /// <summary>
    /// Every half injectable, so the routing can be exercised on a machine with
    /// no panel in it - which is every machine running the tests.
    /// <para>
    /// The seams are the three things the panel half actually does, and nothing
    /// else, so a test can stand in for a laptop and a test can stand in for a
    /// desktop. What cannot be stood in for is a panel, and no amount of faking
    /// changes that: the hardware itself still has to be tried by a person with
    /// a laptop in front of them.
    /// </para>
    /// </summary>
    public CompositeBacklightBus(
        IBacklightBus? external = null,
        Func<bool>? panelAvailable = null,
        Func<MonitorProbe?>? panelProbe = null,
        Func<int, bool>? panelSet = null)
    {
        _external = external ?? new SystemBacklightBus();
        _panelAvailable = panelAvailable ?? PanelWmi.IsAvailable;
        _panelProbe = panelProbe ?? PanelWmi.Probe;
        _panelSet = panelSet ?? PanelWmi.Set;
    }

    public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys)
    {
        List<MonitorProbe> all = new(_external.ProbeAll(excludedDeviceKeys));

        // Excluded exactly the way an external monitor is. The panel is just
        // another display here, so the switch that retired a bad monitor retires
        // a bad panel by the same path with no special case.
        bool excluded = excludedDeviceKeys is not null
            && excludedDeviceKeys.Contains(PanelWmi.DeviceName, StringComparer.OrdinalIgnoreCase);

        if (!excluded && _panelAvailable() && _panelProbe() is { } panel)
        {
            all.Add(panel);
        }

        return all;
    }

    public BusOutcome TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error)
    {
        if (string.Equals(deviceName, PanelWmi.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            why = null;
            win32Error = 0;

            if (!_panelSet((int)Math.Clamp(value, 0, 100)))
            {
                // Refused rather than Failed: the panel was asked and would not
                // take it, which is the case the exclusion list is for. A Failed
                // here would be read as the bus being broken, and three refusals
                // later the panel is quietly written off with nothing to show why.
                why = "the panel did not take the brightness value";
                return BusOutcome.Refused;
            }

            return BusOutcome.Ok;
        }

        return _external.TrySetBrightness(deviceName, value, minimum, maximum, out why, out win32Error);
    }
}
