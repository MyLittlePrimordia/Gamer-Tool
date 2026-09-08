using System;
using System.Collections.Generic;

namespace GamerTool.Models;

/// <summary>
/// A single Audio preset: 10 per-band gains in dB (ordered to match
/// AudioManager.BandFrequenciesHz: 31, 63, 125, 250, 500, 1k, 2k, 4k, 8k,
/// 16kHz) plus whether native Windows Loudness Equalization should be
/// enabled, and an optional bound hotkey.
/// </summary>
public sealed class AudioPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }

    /// <summary>10 values in dB, -12..+12, ordered to match AudioManager.BandFrequenciesHz.</summary>
    public double[] BandGainsDb { get; set; } = new double[10];

    public bool EnableNativeLoudness { get; set; }

    public int? HotkeyId { get; set; }
    public uint HotkeyModifiers { get; set; }
    public uint HotkeyVirtualKey { get; set; }

    public AudioPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        IsBuiltIn = IsBuiltIn,
        BandGainsDb = (double[])BandGainsDb.Clone(),
        EnableNativeLoudness = EnableNativeLoudness,
        HotkeyId = HotkeyId,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey
    };

    /// <summary>
    /// The seven factory Audio presets from the Phase 1 spec. Band order is
    /// [31, 63, 125, 250, 500, 1k, 2k, 4k, 8k, 16k] Hz throughout.
    /// </summary>
    public static List<AudioPreset> CreateBuiltIns()
    {
        return new List<AudioPreset>
        {
            new() { Id = "builtin-default", Name = "Default", IsBuiltIn = true,
                BandGainsDb = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, EnableNativeLoudness = false },

            new() { Id = "builtin-footsteps", Name = "Footsteps & Movement", IsBuiltIn = true,
                BandGainsDb = new double[] { -4, -3, -1, 0, 1, 3, 6, 7, 4, 2 }, EnableNativeLoudness = false },

            new() { Id = "builtin-explosion-damper", Name = "Explosion Damper", IsBuiltIn = true,
                BandGainsDb = new double[] { -6, -5, -3, -1, 0, 0, 0, -1, -2, -2 }, EnableNativeLoudness = true },

            new() { Id = "builtin-latenight", Name = "Late Night (Quiet Mode)", IsBuiltIn = true,
                BandGainsDb = new double[] { -5, -4, -2, 0, 2, 4, 4, 3, 1, 0 }, EnableNativeLoudness = true },

            new() { Id = "builtin-dialogue", Name = "Dialogue & Voice", IsBuiltIn = true,
                BandGainsDb = new double[] { -3, -2, 0, 2, 5, 6, 4, 1, 0, -1 }, EnableNativeLoudness = false },

            new() { Id = "builtin-heavybass", Name = "Heavy Bass & Rumble", IsBuiltIn = true,
                BandGainsDb = new double[] { 8, 7, 5, 2, 0, -1, -1, 0, 0, 0 }, EnableNativeLoudness = false },

            new() { Id = "builtin-crisptreble", Name = "Crisp Treble", IsBuiltIn = true,
                BandGainsDb = new double[] { -2, -1, 0, 0, 0, 1, 3, 6, 7, 6 }, EnableNativeLoudness = false },
        };
    }
}
