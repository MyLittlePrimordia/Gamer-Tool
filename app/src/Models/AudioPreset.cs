using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GamerTool.Models;

/// <summary>
/// A single Audio preset: 10 per-band gains in dB (ordered to match
/// AudioManager.BandFrequenciesHz: 31, 63, 125, 250, 500, 1k, 2k, 4k, 8k,
/// 16kHz) plus whether native Windows Loudness Equalization should be
/// enabled, and an optional bound hotkey. Only the HotkeyId field notifies -
/// the card caption must update live after hotkey recording (see
/// DisplayPreset for the same rationale).
/// </summary>
public sealed class AudioPreset : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }
    private string _name = string.Empty;

    /// <summary>Emoji glyph identifier shown next to the name - never part of Name itself.</summary>
    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconAndName)); }
    }
    private string _icon = string.Empty;

    /// <summary>"{name}" for text-only displays - icons render as separate Images now.</summary>
    public string IconAndName => Name;
    public bool IsBuiltIn { get; set; }

    /// <summary>Sonar-style favorite: starred presets appear in Home/favorites + top of tray.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>10 values in dB, -12..+12, ordered to match AudioManager.BandFrequenciesHz.</summary>
    public double[] BandGainsDb { get; set; } = new double[10];

    public bool EnableNativeLoudness { get; set; }

    /// <summary>Output trim in dB, -12..+12, applied after the 10-band EQ. Default 0 = unchanged.</summary>
    public double PreampDb { get; set; } = 0.0;

    /// <summary>Id returned by HotkeyManager.RegisterHotkey, or null if this preset has no bound hotkey.</summary>
    public int? HotkeyId
    {
        get => _hotkeyId;
        set { _hotkeyId = value; OnPropertyChanged(); OnPropertyChanged(nameof(HotkeyDisplayLabel)); }
    }
    private int? _hotkeyId;

    public uint HotkeyModifiers
    {
        get => _hotkeyModifiers;
        set { _hotkeyModifiers = value; OnPropertyChanged(nameof(HotkeyDisplayLabel)); }
    }
    private uint _hotkeyModifiers;

    public uint HotkeyVirtualKey
    {
        get => _hotkeyVirtualKey;
        set { _hotkeyVirtualKey = value; OnPropertyChanged(nameof(HotkeyDisplayLabel)); }
    }
    private uint _hotkeyVirtualKey;

    /// <summary>Human-readable "Ctrl+Alt+F1" style label, or null when unbound.</summary>
    public string? HotkeyDisplayLabel =>
        HotkeyId.HasValue ? GamerTool.Core.HotkeyFormatting.Format(HotkeyModifiers, HotkeyVirtualKey) : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public AudioPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        IsBuiltIn = IsBuiltIn,
        IsFavorite = IsFavorite,
        BandGainsDb = (double[])BandGainsDb.Clone(),
        EnableNativeLoudness = EnableNativeLoudness,
        PreampDb = PreampDb,
        HotkeyId = HotkeyId,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey
    };

    /// <summary>
    /// The built-in Audio presets, named by game genre with an emoji glyph.
    /// Ids are stable forever so older settings.json files keep matching.
    /// </summary>
    /// <summary>
    /// Built-in Audio presets by genre. Band order is [31, 63, 125, 250,
    /// 500, 1k, 2k, 4k, 8k, 16k] Hz. Curves follow audio-engineering
    /// conventions: footsteps live at 2-6 kHz (presence), engine rumble at
    /// 30-125 Hz (sub/bass), voice intelligibility 1-4 kHz, and harshness
    /// sits above 8 kHz. Icon values are IconCatalog keys.
    /// </summary>
    public static List<AudioPreset> CreateBuiltIns()
    {
        return new List<AudioPreset>
        {
            // Flat reference.
            new() { Id = "builtin-default", Name = "Standard", Icon = "videogame", IsBuiltIn = true,
                BandGainsDb = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, EnableNativeLoudness = false },

            // Competitive footstep focus: cut masking lows, boost 2-8 kHz
            // where footsteps, reloads and pin pulls live. Classic FPS tune.
            new() { Id = "builtin-footsteps", Name = "FPS Shooter", Icon = "bullseye", IsBuiltIn = true,
                BandGainsDb = new double[] { -4, -3, -1, 0, 1, 3, 6, 7, 4, 2 }, EnableNativeLoudness = false },

            // Tame blockbuster booms: low-shelf cut, gentle loudness
            // compression keeps quiet dialogue audible under gunfire.
            new() { Id = "builtin-explosion-damper", Name = "Explosions", Icon = "bomb", IsBuiltIn = true,
                BandGainsDb = new double[] { -6, -5, -3, -1, 0, 0, 0, -1, -2, -2 }, EnableNativeLoudness = true },

            // Voice-forward: lift the intelligibility band, dip the rumble.
            new() { Id = "builtin-dialogue", Name = "Dialogue", Icon = "loudspeaker", IsBuiltIn = true,
                BandGainsDb = new double[] { -3, -2, 0, 2, 5, 6, 4, 1, 0, -1 }, EnableNativeLoudness = false },

            // Racing: engine + tire roar. Substantial sub/bass lift with a
            // gentle presence bump for tire squeal and HUD callouts.
            new() { Id = "builtin-racing", Name = "Racing", Icon = "headphone", IsBuiltIn = true,
                BandGainsDb = new double[] { 7, 6, 5, 2, 0, 0, 1, 2, 1, 0 }, EnableNativeLoudness = false },

            // Fighting game impact: bass punch for hits, slight presence so
            // hit/block SFX stay crisp in the mix.
            new() { Id = "builtin-fighting", Name = "Fighting", Icon = "fire", IsBuiltIn = true,
                BandGainsDb = new double[] { 5, 4, 3, 1, 0, 1, 2, 2, 1, 1 }, EnableNativeLoudness = false },

            // Horror / atmospheric: reduce fatiguing highs, keep low-end
            // dread rumble, slightly forward mids for whispers.
            new() { Id = "builtin-horror", Name = "Horror", Icon = "ghost", IsBuiltIn = true,
                BandGainsDb = new double[] { 2, 2, 1, 0, 1, 2, 1, -1, -3, -4 }, EnableNativeLoudness = false },

            // MOBA / strategy: even voice + HUD clarity, minor sub lift.
            new() { Id = "builtin-moba", Name = "MOBA", Icon = "shield", IsBuiltIn = true,
                BandGainsDb = new double[] { 1, 1, 0, 1, 2, 3, 2, 1, 0, 0 }, EnableNativeLoudness = false },

            // RPG / cinematic score: wide musical lift - warm lows, present
            // mids, airy but controlled highs for orchestral soundtracks.
            new() { Id = "builtin-cinematic", Name = "RPG", Icon = "music", IsBuiltIn = true,
                BandGainsDb = new double[] { 3, 2, 1, 1, 2, 2, 2, 2, 2, 1 }, EnableNativeLoudness = false },

            // Night mode: compression on, rumble cut, everything else flat.
            new() { Id = "builtin-latenight", Name = "Late Night", Icon = "crescent", IsBuiltIn = true,
                BandGainsDb = new double[] { -5, -4, -2, 0, 2, 4, 4, 3, 1, 0 }, EnableNativeLoudness = true },

            // Battle royale: same footstep-forward shape as FPS Shooter but
            // less extreme (vehicles/distant fights still matter), with
            // compression on to catch the huge gap between far-off footsteps
            // and a nearby explosion.
            new() { Id = "builtin-battleroyale", Name = "Battle Royale", Icon = "robot", IsBuiltIn = true,
                BandGainsDb = new double[] { -3, -2, -1, 0, 1, 3, 5, 6, 4, 2 }, EnableNativeLoudness = true },

            // Sports: commentary clarity plus stadium atmosphere - bass for
            // crowd roar and impacts, mid lift for the commentator, gentle
            // high cut so crowd noise doesn't turn harsh.
            new() { Id = "builtin-sports", Name = "Sports", Icon = "trophy", IsBuiltIn = true,
                BandGainsDb = new double[] { 3, 3, 2, 1, 1, 3, 3, 1, 0, -1 }, EnableNativeLoudness = false },

            // Casual / family: gentle "smile" curve - gentle warmth and, no
            // aggressive tuning, just a pleasant sound.
            new() { Id = "builtin-casual", Name = "Casual", Icon = "sun", IsBuiltIn = true,
                BandGainsDb = new double[] { 3, 2, 1, 0, 0, 0, 1, 2, 2, 1 }, EnableNativeLoudness = false },
        };
    }
}
