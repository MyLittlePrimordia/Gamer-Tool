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
public sealed class BacklightService
{
    private AppSettings _settings;

    private readonly Dictionary<string, MonitorProbe> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    private bool _probed;

    public BacklightService(AppSettings settings)
    {
        _settings = settings;
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
    /// </summary>
    public void Probe()
    {
        if (!_settings.HardwareBrightnessEnabled)
        {
            return;
        }

        _byDevice.Clear();

        foreach (MonitorProbe monitor in HardwareBrightness.ProbeAll(_settings.ExcludedDdcMonitors))
        {
            _byDevice[monitor.DeviceName] = monitor;
        }

        _probed = true;

        foreach (MonitorProbe monitor in _byDevice.Values)
        {
            // The probe result is the single most useful line in the log when
            // someone reports that brightness will not move: it says what the
            // driver said, per display, without anyone having to reproduce it.
            AppLog.Info("backlight " + monitor.DeviceName + " " + monitor.FriendlyName
                + " edid=" + monitor.Edid.Summary
                + " capable=" + monitor.CanControlBacklight
                + " reading=" + (monitor.Brightness?.ToString() ?? "none")
                + " reason=" + (monitor.BlockedReason ?? monitor.NoReplyBecause ?? "none"));
        }

        Probed?.Invoke();
    }


    public bool HasProbed => _probed;


    public MonitorProbe? Find(string deviceName) =>
        _byDevice.TryGetValue(deviceName, out MonitorProbe? probe) ? probe : null;


    /// <summary>
    /// What a display can be told, in its own terms. Unsupported means exactly
    /// what it says: this display, not this machine.
    /// </summary>
    public string CapabilityOf(MonitorProbe monitor)
    {
        if (monitor.BlockedReason is not null)
        {
            return "Not supported - " + monitor.BlockedReason;
        }

        return monitor.CanControlBacklight
            ? "Supported"
            : "Not supported - " + (monitor.NoReplyBecause ?? "the monitor did not answer");
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
    /// </summary>
    public bool TrySet(MonitorProbe monitor, uint value, out string? why)
    {
        why = null;

        if (!monitor.CanControlBacklight || monitor.Brightness is null)
        {
            why = "this display does not support hardware brightness";
            return false;
        }

        if (!_settings.OriginalHardwareBrightness.ContainsKey(monitor.DeviceName))
        {
            _settings.OriginalHardwareBrightness[monitor.DeviceName] = monitor.Brightness.Current;
        }

        if (!HardwareBrightness.TrySetBrightness(
                monitor.DeviceName,
                value,
                monitor.Brightness.Minimum,
                monitor.Brightness.Maximum,
                out why))
        {
            AppLog.Warn("backlight write refused on " + monitor.DeviceName + ": " + (why ?? "unknown"));

            int count = _refusals.TryGetValue(monitor.DeviceName, out int seen) ? seen + 1 : 1;
            _refusals[monitor.DeviceName] = count;

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
                return false;
            }

            // A run of them. Now the display is put on the exclusion list so the
            // next launch does not probe it either, and the reading is cleared so
            // the row greys out rather than failing every remaining drag.
            if (!_settings.ExcludedDdcMonitors.Contains(monitor.DeviceName))
            {
                _settings.ExcludedDdcMonitors.Add(monitor.DeviceName);
                _settingsChanged = true;
            }

            monitor.NoReplyBecause = why;
            monitor.Brightness = null;
            return false;
        }

        _refusals.Remove(monitor.DeviceName);
        monitor.NoReplyBecause = null;

        monitor.Brightness = new BrightnessReading
        {
            Minimum = monitor.Brightness.Minimum,
            Current = Math.Clamp(value, monitor.Brightness.Minimum, monitor.Brightness.Maximum),
            Maximum = monitor.Brightness.Maximum
        };

        return true;
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
            HardwareBrightness.TrySetBrightness(device, original, 0, maximum, out _);
        }

        _settings.OriginalHardwareBrightness.Clear();
    }
}
