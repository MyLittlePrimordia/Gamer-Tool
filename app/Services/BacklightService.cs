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
    private readonly AppSettings _settings;

    private readonly Dictionary<string, MonitorProbe> _byDevice = new(StringComparer.OrdinalIgnoreCase);

    private bool _probed;

    public BacklightService(AppSettings settings)
    {
        _settings = settings;
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
            // A display that refuses a write is one this app should stop asking.
            // It goes on the exclusion list so the next launch does not probe it
            // either, and the row greys out rather than failing every drag.
            if (!_settings.ExcludedDdcMonitors.Contains(monitor.DeviceName))
            {
                _settings.ExcludedDdcMonitors.Add(monitor.DeviceName);
            }

            // Cleared, not just flagged: with no reading left the row greys out
            // and CanControlBacklight goes false on its own, so there is no way
            // for the slider to stay live on a display that just refused us.
            monitor.NoReplyBecause = why;
            monitor.Brightness = null;
            return false;
        }

        monitor.Brightness = new BrightnessReading
        {
            Minimum = monitor.Brightness.Minimum,
            Current = Math.Clamp(value, monitor.Brightness.Minimum, monitor.Brightness.Maximum),
            Maximum = monitor.Brightness.Maximum
        };

        return true;
    }


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
