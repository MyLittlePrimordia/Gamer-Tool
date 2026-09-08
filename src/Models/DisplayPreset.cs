using System;
using System.Collections.Generic;

namespace GamerTool.Models;

/// <summary>
/// A single Display (gamma/contrast/color) preset: the seven parameters fed
/// straight into DisplayManager.ComputeRamp, plus an optional bound hotkey.
/// Plain data only - no INotifyPropertyChanged, since the UI binds sliders to
/// flattened properties on MainViewModel rather than directly to these
/// objects, and Name/values only ever change by replacing or explicitly
/// re-saving a preset (never silently mutating one already on screen).
/// </summary>
public sealed class DisplayPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }

    public double Gamma { get; set; } = 1.0;
    public double Contrast { get; set; } = 1.0;
    public double ShadowLift { get; set; } = 0.0;
    public double BrightnessOffset { get; set; } = 0.0;
    public double GainRed { get; set; } = 1.0;
    public double GainGreen { get; set; } = 1.0;
    public double GainBlue { get; set; } = 1.0;

    /// <summary>Id returned by HotkeyManager.RegisterHotkey, or null if this preset has no bound hotkey.</summary>
    public int? HotkeyId { get; set; }
    public uint HotkeyModifiers { get; set; }
    public uint HotkeyVirtualKey { get; set; }

    public DisplayPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        IsBuiltIn = IsBuiltIn,
        Gamma = Gamma,
        Contrast = Contrast,
        ShadowLift = ShadowLift,
        BrightnessOffset = BrightnessOffset,
        GainRed = GainRed,
        GainGreen = GainGreen,
        GainBlue = GainBlue,
        HotkeyId = HotkeyId,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey
    };

    /// <summary>
    /// The seven factory Display presets from the Phase 1 spec, with concrete
    /// seed values chosen to match each preset's stated purpose (e.g. Dark
    /// Scenes lifts shadow detail, Night Eye Comfort pulls back blue gain).
    /// </summary>
    public static List<DisplayPreset> CreateBuiltIns()
    {
        return new List<DisplayPreset>
        {
            new() { Id = "builtin-default", Name = "Default", IsBuiltIn = true,
                Gamma = 1.00, Contrast = 1.00, ShadowLift = 0.00, BrightnessOffset = 0.00,
                GainRed = 1.00, GainGreen = 1.00, GainBlue = 1.00 },

            new() { Id = "builtin-dark-scenes", Name = "Dark Scenes", IsBuiltIn = true,
                Gamma = 1.18, Contrast = 1.05, ShadowLift = 0.20, BrightnessOffset = 0.02,
                GainRed = 1.00, GainGreen = 1.00, GainBlue = 1.00 },

            new() { Id = "builtin-spotter", Name = "Footstep / Enemy Spotter", IsBuiltIn = true,
                Gamma = 1.05, Contrast = 1.30, ShadowLift = 0.12, BrightnessOffset = 0.00,
                GainRed = 1.08, GainGreen = 1.05, GainBlue = 1.00 },

            new() { Id = "builtin-cinematic", Name = "Cinematic & Story", IsBuiltIn = true,
                Gamma = 1.00, Contrast = 1.22, ShadowLift = 0.04, BrightnessOffset = -0.03,
                GainRed = 1.06, GainGreen = 1.00, GainBlue = 0.94 },

            new() { Id = "builtin-vibrant", Name = "Vibrant World", IsBuiltIn = true,
                Gamma = 0.95, Contrast = 1.12, ShadowLift = 0.02, BrightnessOffset = 0.01,
                GainRed = 1.15, GainGreen = 1.15, GainBlue = 1.15 },

            new() { Id = "builtin-nighteye", Name = "Night Eye Comfort", IsBuiltIn = true,
                Gamma = 1.08, Contrast = 0.95, ShadowLift = 0.06, BrightnessOffset = -0.02,
                GainRed = 1.02, GainGreen = 0.98, GainBlue = 0.75 },

            new() { Id = "builtin-brightroom", Name = "Bright Room / Sunlight", IsBuiltIn = true,
                Gamma = 0.85, Contrast = 1.08, ShadowLift = 0.03, BrightnessOffset = 0.12,
                GainRed = 1.02, GainGreen = 1.02, GainBlue = 1.00 },
        };
    }
}
