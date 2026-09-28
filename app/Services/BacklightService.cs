using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Owns the hardware backlight feature for the window: when it is allowed to
/// probe, what each display can do, and writing a new value.
/// <para>
/// Everything here is per display. There is no "does this machine support
/// DDC/CI" flag anywhere, because that question has no single answer: one
/// display can answer VCP 0x10 while the one next to it has no pathway at all,
/// and a shared flag would disable a working monitor because an unlucky one was
/// plugged in.
/// </para>
/// </summary>
/// <summary>
/// Everything <see cref="BacklightService"/> needs from the hardware, behind a
/// seam.
/// <para>
/// The service otherwise talks straight to a static class that opens an I2C bus,
/// which makes the interesting part of it untestable: the refusal counting, the
/// exclusion list and the busy-skip handling are all pure bookkeeping over
/// answers, and none of it can be exercised without a monitor on the desk. This
/// exists so those rules can be pinned down in CI, and so no test has to be
/// written that touches real hardware.
/// </para>
/// </summary>
public interface IBacklightBus
{
    IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys);

    BusOutcome TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error);
}


/// <summary>The real bus. A thin wrapper so the service has one thing to hold.</summary>
public sealed class SystemBacklightBus : IBacklightBus
{
    public IReadOnlyList<MonitorProbe> ProbeAll(IReadOnlyCollection<string>? excludedDeviceKeys) =>
        HardwareBrightness.ProbeAll(excludedDeviceKeys);

    public BusOutcome TrySetBrightness(
        string deviceName,
        uint value,
        uint minimum,
        uint maximum,
        out string? why,
        out int win32Error) =>
        HardwareBrightness.TrySetBrightness(deviceName, value, minimum, maximum, out why, out win32Error);
}


/// <summary>
/// How many times a write is re-sent when the bus was busy rather than refused.
/// <para>
/// Pulled out of the window so it can be tested. The policy is small but it is
/// the difference between a slider that lands where it was left and one that
/// silently does nothing: a write that collided with another monitor's
/// transaction used to be dropped on the floor, the row still showed the new
/// value, and the panel stayed where it was with no error anywhere.
/// </para>
/// <para>
/// The budget is one, deliberately. A retry once handles the ordinary case,
/// where a probe of the next monitor was in flight for a few milliseconds. An
/// unbounded retry would keep re-queueing against a bus that is wedged by a
/// monitor that has stopped answering, which is the one situation where a
/// brightness value must not be chasing the panel forever.
/// </para>
/// </summary>
public sealed class BacklightWriteRetry
{
    /// <summary>Re-sends allowed per display before the value is given up on.</summary>
    public const int MaxRetries = 1;

    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this display may be re-sent now. Counts as consumed when it
    /// says yes, so a second busy answer for the same display says no and the
    /// caller reports the failure instead of looping.
    /// </summary>
    public bool ShouldRetry(string deviceName)
    {
        int used = _attempts.TryGetValue(deviceName, out int seen) ? seen : 0;

        if (used >= MaxRetries)
        {
            return false;
        }

        _attempts[deviceName] = used + 1;
        return true;
    }

    /// <summary>Called on any answer that was not a skip, including a success.</summary>
    public void Reset(string deviceName)
    {
        _attempts.Remove(deviceName);
    }

    /// <summary>Retries still unspent, for the log.</summary>
    public int Remaining(string deviceName) =>
        _attempts.TryGetValue(deviceName, out int used) ? Math.Max(0, MaxRetries - used) : MaxRetries;
}


public sealed class BacklightService
{
    private AppSettings _settings;

    private readonly IBacklightBus _bus;

    private readonly Dictionary<string, MonitorProbe> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    private bool _probed;

    public BacklightService(AppSettings settings, IBacklightBus? bus = null)
    {
        _settings = settings;
        _bus = bus ?? new SystemBacklightBus();
    }

    /// <summary>
    /// Points the service at a different settings object.
    /// <para>
    /// The service holds the profile it was built with, so a restore that swaps
    /// the whole object for the incoming one leaves this reading and writing an
    /// orphan: the enabled flag stops reflecting reality, and the remembered
    /// brightness and the exclusion list are written somewhere nothing ever
    /// saves. Rebinding is what keeps one profile the single source of truth.
    /// </para>
    /// </summary>
    public void Rebind(AppSettings settings)
    {
        if (settings is not null)
        {
            _settings = settings;
        }
    }


    /// <summary>Raised after a probe finishes, so the row can redraw itself.</summary>
    public event Action? Probed;


    public bool IsEnabled => _settings.HardwareBrightnessEnabled;


    public IReadOnlyList<MonitorProbe> Monitors => _byDevice.Values.ToList();


    /// <summary>
    /// Reads every display once. Only ever called when the user has opted in, and
    /// never at startup: the first thing this app should not do is touch the I2C
    /// bus before being asked.
    /// <para>
    /// Single flight on purpose. The probe is started from two places, the setup
    /// tab and the tab switch, and the old guard was only set once a probe had
    /// finished, so two could start together. They then cleared and repopulated
    /// the same dictionary underneath each other, and whichever finished last won:
    /// a half filled table, or a display that had been probed and then dropped
    /// without ever having been asked anything.
    /// </para>
    /// </summary>
    public void Probe()
    {
        if (!_settings.HardwareBrightnessEnabled)
        {
            return;
        }

        lock (_probeGate)
        {
            if (_probing)
            {
                // Someone else is already asking the monitors. Joining them is
                // both cheaper and safer than starting a second pass.
                AppLog.Info("backlight probe already running, not starting another");
                return;
            }

            _probing = true;
        }

        try
        {
            _byDevice.Clear();

            foreach (MonitorProbe monitor in _bus.ProbeAll(_settings.ExcludedDdcMonitors))
            {
                _byDevice[monitor.DeviceName] = monitor;
            }

            _probed = true;

            foreach (MonitorProbe monitor in _byDevice.Values)
            {
                // The probe result is the single most useful line in the log when
                // someone reports that brightness will not move: it says what the
                // driver said, per display, without anyone having to reproduce it.
                // The stage reached and the error are in there deliberately, so
                // the difference between "no handle from the driver" and "declined
                // the code" is readable without a debugger.
                AppLog.Info("backlight " + monitor.DeviceName + " " + monitor.FriendlyName
                    + " edid=" + monitor.Edid.Summary
                    + " edidFrom=" + monitor.EdidSource
                    + " link=" + monitor.Link
                    + " capable=" + monitor.CanControlBacklight
                    + " reading=" + (monitor.Brightness?.ToString() ?? "none")
                    + " stage=" + StageOf(monitor)
                    + " outcome=" + monitor.Outcome
                    + " win32=" + (monitor.LastError ?? "none")
                    + " attempts=" + monitor.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " probeMs=" + monitor.ProbeMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " phys=" + (monitor.PhysicalMonitor ?? "none")
                    + " excluded=" + _settings.ExcludedDdcMonitors.Contains(monitor.DeviceName)
                    + " refusals=" + RefusalCountOf(monitor.DeviceName).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " reason=" + (monitor.BlockedReason ?? monitor.NoReplyBecause ?? "none"));
            }
        }
        finally
        {
            lock (_probeGate)
            {
                _probing = false;
            }
        }

        Probed?.Invoke();
    }


    private readonly object _probeGate = new();

    private bool _probing;

    private int RefusalCountOf(string deviceName) =>
        _refusals.TryGetValue(deviceName, out int seen) ? seen : 0;

    /// <summary>
    /// The furthest point the probe got, in words. This is the difference between
    /// a monitor that said no and a machine that never asked, and it is the first
    /// thing worth reading when a display is greyed out for no visible reason.
    /// </summary>
    public static string StageOf(MonitorProbe monitor)
    {
        if (monitor.BlockedReason is not null)
        {
            return monitor.BlockedReason.StartsWith("on your exclusion", StringComparison.Ordinal)
                ? "excluded"
                : "edid-preflight";
        }

        return monitor.Outcome switch
        {
            BusOutcome.Ok => "vcp-read-ok",
            BusOutcome.NoDdcPathway => "no-ddc-handle",
            BusOutcome.Refused => "vcp-declined",
            BusOutcome.TimedOut => "timeout",
            BusOutcome.Busy => "bus-busy",
            _ => "failed"
        };
    }


    public bool HasProbed => _probed;


    public MonitorProbe? Find(string deviceName) =>
        _byDevice.TryGetValue(deviceName, out MonitorProbe? probe) ? probe : null;

    /// <summary>
    /// What the row shows the user. Deliberately short and free of anything a
    /// person cannot act on: the row used to print the whole refusal string,
    /// which meant a tooltip reading "declined 0x10 after 3 attempts, error
    /// 0xC0262581" on a row whose badge reads Not Supported. The technical half
    /// is in the log and in Copy diagnostics, where it can be pasted into a bug
    /// report, which is the only place it was ever any use.
    /// </summary>
    public string CapabilityOf(MonitorProbe monitor)
    {
        if (monitor.CanControlBacklight)
        {
            return "Supported";
        }

        if (monitor.BlockedReason is not null
            && monitor.BlockedReason.StartsWith("on your exclusion", StringComparison.Ordinal))
        {
            return "Not supported - switch Hardware brightness off and on to try again";
        }

        return "This monitor doesn't support hardware brightness.";
    }


    /// <summary>
    /// How many refusals in a row before a display is put on the exclusion list.
    /// <para>
    /// One is not enough, and the reason is written all over the code that talks
    /// to these panels: a dropped first packet is normal, three attempts are made
    /// before a read is called a refusal, and a two second timeout is reported as
    /// a wedged bus. A single failure is therefore an everyday event on hardware
    /// that is otherwise perfectly good, and treating it as proof that a display
    /// is dangerous threw the feature away for a monitor that would have worked
    /// on the next drag. Excluding is still right eventually, because a panel
    /// that keeps refusing is a panel to leave alone, but it now takes a run of
    /// them.
    /// </para>
    /// </summary>
    private const int RefusalsBeforeExclusion = 3;

    private readonly Dictionary<string, int> _refusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Consecutive refusals per display, for the badge and the log.</summary>
    public IReadOnlyDictionary<string, int> Refusals => _refusals;

    /// <summary>
    /// Writes a new brightness. The first time a display is touched its current
    /// value is remembered so it can be handed back on the way out.
    /// <para>
    /// Returns the outcome rather than a bool, because the caller has to tell
    /// three things apart: it landed, the display refused, or the bus was busy
    /// and the display was never asked. Collapsing those into a false is what
    /// made a skipped write vanish, since the only sensible thing to do with a
    /// bool false is to show the user that it failed.
    /// </para>
    /// </summary>
    public BusOutcome TrySet(MonitorProbe monitor, uint value, out string? why)
    {
        why = null;

        if (!monitor.CanControlBacklight || monitor.Brightness is null)
        {
            why = "this display does not support hardware brightness";
            return BusOutcome.Failed;
        }

        if (!_settings.OriginalHardwareBrightness.ContainsKey(monitor.DeviceName))
        {
            _settings.OriginalHardwareBrightness[monitor.DeviceName] = monitor.Brightness.Current;
        }

        BusOutcome outcome = _bus.TrySetBrightness(
            monitor.DeviceName,
            value,
            monitor.Brightness.Minimum,
            monitor.Brightness.Maximum,
            out why,
            out int win32Error);

        if (outcome == BusOutcome.Busy)
        {
            // Not a refusal, and not the display's fault. Nothing is recorded and
            // nothing is cleared: the reading stays live, and the caller is
            // expected to send the same value again rather than leaving the row
            // showing a brightness the panel is not actually at.
            return BusOutcome.Busy;
        }

        if (outcome != BusOutcome.Ok)
        {
            string detail = win32Error == 0
                ? why ?? "unknown"
                : (why ?? "unknown") + " [" + DdcErrors.Describe(win32Error) + "]";

            AppLog.Warn("backlight write on " + monitor.DeviceName + " ended " + outcome
                + ": " + detail);

            int count = _refusals.TryGetValue(monitor.DeviceName, out int seen) ? seen + 1 : 1;
            _refusals[monitor.DeviceName] = count;
            monitor.Outcome = outcome;

            if (count < RefusalsBeforeExclusion)
            {
                // Not the end of it. The reading is kept so the row stays live and
                // the next drag can still try, and the reason is recorded so the
                // badge can say the display is being difficult rather than just
                // going quiet.
                monitor.NoReplyBecause = why;
                why = (why ?? "the monitor refused the value")
                    + " (attempt " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " of " + RefusalsBeforeExclusion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", will try again)";
                return outcome;
            }

            // A run of them. Now the display is put on the exclusion list so the
            // next launch does not probe it either, and the reading is cleared so
            // the row greys out rather than failing every remaining drag. The
            // Hardware brightness switch clears this again, so it is a pause
            // rather than a life sentence.
            if (!_settings.ExcludedDdcMonitors.Contains(monitor.DeviceName))
            {
                _settings.ExcludedDdcMonitors.Add(monitor.DeviceName);
                _settingsChanged = true;
                AppLog.Warn("backlight excluding " + monitor.FriendlyOrDevice()
                    + " after " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " refusals: " + detail
                    + ". Turn the Hardware brightness switch off and on to clear this.");
            }

            monitor.NoReplyBecause = why;
            monitor.Brightness = null;
            return outcome;
        }

        _refusals.Remove(monitor.DeviceName);
        monitor.NoReplyBecause = null;
        monitor.Outcome = BusOutcome.Ok;

        monitor.Brightness = new BrightnessReading
        {
            Minimum = monitor.Brightness.Minimum,
            Current = Math.Clamp(value, monitor.Brightness.Minimum, monitor.Brightness.Maximum),
            Maximum = monitor.Brightness.Maximum,
            CodeType = monitor.Brightness.CodeType
        };

        return BusOutcome.Ok;
    }


    /// <summary>
    /// Forgets every exclusion and every refusal count, and allows a fresh probe.
    /// <para>
    /// Wired to the Hardware brightness switch going from off to on. The
    /// exclusion list is persisted, so before this a monitor that had been
    /// written off once stayed written off for good with no way back short of
    /// editing settings.json by hand, and switching the feature off and on again
    /// did nothing at all because the probe guard was still set.
    /// </para>
    /// </summary>
    public void ForgetExclusions()
    {
        bool hadAny = _settings.ExcludedDdcMonitors.Count > 0 || _refusals.Count > 0;

        foreach (string device in _settings.ExcludedDdcMonitors.ToList())
        {
            AppLog.Info("backlight clearing exclusion for " + device);
        }

        _settings.ExcludedDdcMonitors.Clear();
        _refusals.Clear();
        _byDevice.Clear();
        _probed = false;

        if (hadAny)
        {
            _settingsChanged = true;
            AppLog.Info("backlight exclusions and refusal counts cleared, will probe again");
        }
    }

    /// <summary>
    /// True when <see cref="ExcludedDdcMonitors"/> has grown and the profile has
    /// not been written yet. The window commits once this is set, rather than the
    /// exclusion being written to memory and then quietly forgotten, which is
    /// what used to happen.
    /// </summary>
    public bool ConsumeSettingsChanged()
    {
        bool changed = _settingsChanged;
        _settingsChanged = false;
        return changed;
    }

    private bool _settingsChanged;


    /// <summary>
    /// Puts every display back where it was found. Called on the way out, and by
    /// the crash handler, because the one unacceptable outcome here is a monitor
    /// left blindingly bright or unreadably dim because the app died.
    /// </summary>
    public void RestoreAll()
    {
        if (_settings.OriginalHardwareBrightness.Count == 0)
        {
            return;
        }

        foreach ((string device, uint original) in _settings.OriginalHardwareBrightness.ToList())
        {
            MonitorProbe? monitor = Find(device);
            uint maximum = monitor?.Brightness?.Maximum ?? Math.Max(original, 100);

            // No UI, no opt-in check, no probing: this runs while the app is
            // already on its way out and has to work with what is already known.
            // The outcome is ignored on purpose. A display that will not take its
            // value back on the way out is a problem worth a line in the log, but
            // not one to be shouted about while the app is closing.
            BusOutcome outcome = _bus.TrySetBrightness(device, original, 0, maximum, out string? why, out int win32Error);

            if (outcome != BusOutcome.Ok)
            {
                AppLog.Warn("backlight could not restore " + device + " to "
                    + original.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": " + outcome + " " + (why ?? string.Empty)
                    + (win32Error == 0 ? string.Empty : " [" + DdcErrors.Describe(win32Error) + "]"));
            }
        }

        _settings.OriginalHardwareBrightness.Clear();
    }
}
