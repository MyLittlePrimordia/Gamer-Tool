using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GamerTool.Models;

/// <summary>
/// A single Display (gamma/contrast/color) preset: the seven parameters fed
/// straight into DisplayManager.ComputeRamp, plus an optional bound hotkey.
/// Slider values bind to flattened properties on MainViewModel rather than
/// to these objects, so only the Hotkey fields notify - the card caption
/// ("Ctrl + Alt + F1") must update live right after a recording finishes.
/// </summary>
public sealed class DisplayPreset : INotifyPropertyChanged
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

    public double Gamma { get; set; } = 1.0;
    public double Contrast { get; set; } = 1.0;
    public double ShadowLift { get; set; } = 0.0;
    public double BrightnessOffset { get; set; } = 0.0;
    public double GainRed { get; set; } = 1.0;
    public double GainGreen { get; set; } = 1.0;
    public double GainBlue { get; set; } = 1.0;

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

    /// <summary>Human-readable "Ctrl+Alt+F1" style label, or null when unbound - see NativeMethods for modifier bit meanings.</summary>
    public string? HotkeyDisplayLabel =>
        HotkeyId.HasValue ? GamerTool.Core.HotkeyFormatting.Format(HotkeyModifiers, HotkeyVirtualKey) : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public DisplayPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        IsBuiltIn = IsBuiltIn,
        IsFavorite = IsFavorite,
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
    /// The built-in Display presets, named by game genre with an emoji glyph
    /// (short, generic, instantly recognizable in the compact cycler UI).
    /// Ids are stable forever so older settings.json files keep matching.
    /// </summary>
    /// <summary>
    /// Built-in Display presets by game genre. Values are tuned against
    /// common practice: competitive FPS favors contrast + shadow lift to
    /// spot enemies; horror wants gamma lift without flattening mood;
    /// racing wants vivid saturation; late-session presets cut blue light.
    /// Ids are stable forever so existing settings.json files keep matching.
    /// Icon values are IconCatalog keys (embedded color PNGs).
    /// </summary>
    public static List<DisplayPreset> CreateBuiltIns()
    {
        return new List<DisplayPreset>
        {
            // Neutral reference point - everything else is relative to this.
            new() { Id = "builtin-default", Name = "Standard", Icon = "videogame", IsBuiltIn = true,
                Gamma = 1.00, Contrast = 1.00, ShadowLift = 0.00, BrightnessOffset = 0.00,
                GainRed = 1.00, GainGreen = 1.00, GainBlue = 1.00 },

            // Competitive shooters: enemy silhouettes pop from dark cover.
            // Contrast 1.30 is the sweet spot before banding; shadow lift
            // pulls detail out of doorways without greying true blacks.
            new() { Id = "builtin-spotter", Name = "FPS Shooter", Icon = "bullseye", IsBuiltIn = true,
                Gamma = 1.05, Contrast = 1.30, ShadowLift = 0.12, BrightnessOffset = 0.00,
                GainRed = 1.08, GainGreen = 1.05, GainBlue = 1.00 },

            // Horror / dark sci-fi: heavy gamma + shadow lift to see in
            // near-black scenes, mild blue cut keeps the eerie tone.
            new() { Id = "builtin-dark-scenes", Name = "Horror", Icon = "ghost", IsBuiltIn = true,
                Gamma = 1.22, Contrast = 1.05, ShadowLift = 0.20, BrightnessOffset = 0.02,
                GainRed = 1.02, GainGreen = 1.00, GainBlue = 0.92 },

            // Story / RPG: filmic warm grade, deeper blacks, slightly pulled
            // highlights - the "cinematic" look without crushing detail.
            new() { Id = "builtin-cinematic", Name = "RPG", Icon = "crossswords", IsBuiltIn = true,
                Gamma = 1.00, Contrast = 1.22, ShadowLift = 0.04, BrightnessOffset = -0.03,
                GainRed = 1.06, GainGreen = 1.00, GainBlue = 0.94 },

            // Fighting: busy bright stages need motion clarity - high
            // contrast, slight brightness, neutral colors (frames matter).
            new() { Id = "builtin-fighting", Name = "Fighting", Icon = "fire", IsBuiltIn = true,
                Gamma = 1.00, Contrast = 1.25, ShadowLift = 0.05, BrightnessOffset = 0.03,
                GainRed = 1.02, GainGreen = 1.02, GainBlue = 1.02 },

            // Racing: vivid saturation + brightness for sun-drenched tracks
            // and dusk circuits; RGB gain across the board saturates livery colors.
            new() { Id = "builtin-vibrant", Name = "Racing", Icon = "sun", IsBuiltIn = true,
                Gamma = 0.95, Contrast = 1.12, ShadowLift = 0.02, BrightnessOffset = 0.02,
                GainRed = 1.15, GainGreen = 1.15, GainBlue = 1.15 },

            // MOBA / strategy: readability of map + HUD over drama - mild
            // contrast bump, tiny shadow lift, fully neutral colors.
            new() { Id = "builtin-moba", Name = "MOBA", Icon = "shield", IsBuiltIn = true,
                Gamma = 1.02, Contrast = 1.10, ShadowLift = 0.06, BrightnessOffset = 0.00,
                GainRed = 1.00, GainGreen = 1.00, GainBlue = 1.00 },

            // Long-range precision: moderate gamma + contrast reveal distant
            // ridgeline detail without the heavy lift of the horror preset.
            new() { Id = "builtin-sniper", Name = "Sniper", Icon = "owl", IsBuiltIn = true,
                Gamma = 1.10, Contrast = 1.15, ShadowLift = 0.08, BrightnessOffset = 0.01,
                GainRed = 1.00, GainGreen = 1.00, GainBlue = 0.98 },

            // Sunlit rooms wash out panels - brightness + slight contrast
            // compensate for ambient light glare.
            new() { Id = "builtin-brightroom", Name = "Bright Room", Icon = "trophy", IsBuiltIn = true,
                Gamma = 0.85, Contrast = 1.08, ShadowLift = 0.03, BrightnessOffset = 0.12,
                GainRed = 1.02, GainGreen = 1.02, GainBlue = 1.00 },

            // Late sessions: pull blue hard (sleep-friendlier), gentle gamma.
            new() { Id = "builtin-nighteye", Name = "Night Eye", Icon = "crescent", IsBuiltIn = true,
                Gamma = 1.08, Contrast = 0.95, ShadowLift = 0.06, BrightnessOffset = -0.02,
                GainRed = 1.04, GainGreen = 1.00, GainBlue = 0.75 },
        };
    }
}
