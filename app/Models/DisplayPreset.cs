using System;
using System.Collections.Generic;

namespace GamerTool.Models;

public sealed class DisplayPreset
{
    /// <summary>Per-level red/green/blue trim applied on top of the preset's own gains.</summary>
    public static readonly double[] BlueLightWarm = { 1.05, 1.00, 0.88 };

    public static readonly double[] BlueLightExtraWarm = { 1.09, 1.00, 0.78 };

    public static readonly string[] BlueLightNames = { "OFF", "WARM", "EXTRA WARM" };

    public const double ChannelGainMin = 0.70;

    public const double ChannelGainMax = 1.30;

    /// <summary>The gamma a preset can hold, matching the slider's own range.</summary>
    public const double GammaMin = 1.0;

    public const double GammaMax = 3.0;

    /// <summary>Percentage ranges, matching the sliders.</summary>
    public const double ShadowMin = 0.0;

    public const double ShadowMax = 100.0;

    public const double BrightnessMin = -50.0;

    public const double BrightnessMax = 50.0;

    public const double ContrastMin = -50.0;

    public const double ContrastMax = 50.0;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public double Gamma { get; set; } = 2.20;

    public double ShadowBoost { get; set; }

    public double Brightness { get; set; }

    public double Contrast { get; set; }

    public double RedGain { get; set; } = 1.00;

    public double GreenGain { get; set; } = 1.00;

    public double BlueGain { get; set; } = 1.00;

    public DisplayPreset Copy()
    {
        return new DisplayPreset
        {
            Id = Id,
            Name = Name,
            Tag = Tag,
            Gamma = Gamma,
            ShadowBoost = ShadowBoost,
            Brightness = Brightness,
            Contrast = Contrast,
            RedGain = RedGain,
            GreenGain = GreenGain,
            BlueGain = BlueGain
        };
    }

    /// <summary>
    /// Pulls every value back inside the range its own slider enforces.
    /// <para>
    /// A settings file or a restored backup is data from outside, and the sliders
    /// are the app's own statement of what is legal. Without this a gamma of a
    /// thousand collapses the whole tone curve to white, and there is no value
    /// the user could have typed into the app to produce it, so nothing on screen
    /// explains what happened. Clamping is silent but it is also invisible: the
    /// worst case becomes a slightly wrong picture instead of an unusable one.
    /// </para>
    /// </summary>
    public DisplayPreset Clamp()
    {
        Gamma = double.IsFinite(Gamma) ? Math.Clamp(Gamma, GammaMin, GammaMax) : GammaMin;
        ShadowBoost = double.IsFinite(ShadowBoost) ? Math.Clamp(ShadowBoost, ShadowMin, ShadowMax) : 0.0;
        Brightness = double.IsFinite(Brightness) ? Math.Clamp(Brightness, BrightnessMin, BrightnessMax) : 0.0;
        Contrast = double.IsFinite(Contrast) ? Math.Clamp(Contrast, ContrastMin, ContrastMax) : 0.0;
        RedGain = double.IsFinite(RedGain) ? Math.Clamp(RedGain, ChannelGainMin, ChannelGainMax) : 1.0;
        GreenGain = double.IsFinite(GreenGain) ? Math.Clamp(GreenGain, ChannelGainMin, ChannelGainMax) : 1.0;
        BlueGain = double.IsFinite(BlueGain) ? Math.Clamp(BlueGain, ChannelGainMin, ChannelGainMax) : 1.0;
        return this;
    }

    public static DisplayPreset WithBlueLight(DisplayPreset source, int level)
    {
        DisplayPreset result = source.Copy();
        result.Id = source.Id;
        if (level <= 0 || level > 2)
        {
            return result;
        }

        double[] trim = level == 1 ? BlueLightWarm : BlueLightExtraWarm;
        result.RedGain = Math.Clamp(result.RedGain * trim[0], 0.0, 2.0);
        result.GreenGain = Math.Clamp(result.GreenGain * trim[1], 0.0, 2.0);
        result.BlueGain = Math.Clamp(result.BlueGain * trim[2], 0.0, 2.0);
        return result;
    }

    /// <summary>
    /// The no-op tune: nothing lifted, nothing cut, colour untouched. This is a
    /// real built-in rather than a hidden value, so it sits at the top of the
    /// dropdown where every other app puts its neutral setting, and the reset
    /// button simply selects it. The id is stable, so saved slots pointing at
    /// "flat" keep resolving.
    ///
    /// Named Standard rather than Flat because that is the word for an unmodified
    /// picture, in the same way Windows calls untouched colour "standard" sRGB.
    /// Flat stays the name on the audio side, where it is the established EQ term.
    /// </summary>
    public static DisplayPreset Flat()
    {
        return new DisplayPreset
        {
            Id = "flat",
            Name = "Standard",
            Tag = "No change",
            Gamma = 1.00,
            ShadowBoost = 0.0,
            Brightness = 0.0,
            Contrast = 0.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        };
    }

    // Gamma here is a ramp multiplier where 1.00 is untouched, which is how
    // competitive OSDs treat it. The "look" therefore comes from shadow boost
    // (a black equalizer lifting the dark end) plus a small contrast move,
    // rather than a heavy gamma lift that washes out the blacks.
    //
    // Values follow what esports shooters, racing sims and colour critical
    // workflows actually converge on: low brightness and raised shadows so
    // nothing hides in the blacks, a slightly negative contrast to stop the
    // raised shadows greying the image, and colour left on sRGB unless the
    // preset is specifically about colour. Ids are kept stable so existing
    // saved presets and slots keep pointing at the same entry.
    public static IReadOnlyList<DisplayPreset> Defaults { get; } = new List<DisplayPreset>
    {
        // First on purpose: Flat is the "nothing applied" state every other preset
        // is measured against, and it is what the reset button lands on. It shows
        // in the list as Standard, the name people actually look for.
        Flat(),

        // Esports baseline. Most of the work is the shadow lift, so a player
        // separates from a dark background, with gamma just enough to keep the
        // midtones from going muddy. The slight green lift is worth the fraction
        // of a stop because it reads as brighter without touching contrast.
        new DisplayPreset
        {
            Id = "camper",
            Name = "Competitive",
            Tag = "Esports",
            Gamma = 1.10,
            ShadowBoost = 60.0,
            Brightness = 5.0,
            Contrast = 10.0,
            RedGain = 1.00,
            GreenGain = 1.02,
            BlueGain = 0.98
        },

        // In-game night vision. This is the heaviest shadow lift in the list on
        // purpose: the whole job is finding someone in an unlit room, which is a
        // different problem from being comfortable in a dark room.
        new DisplayPreset
        {
            Id = "night",
            Name = "Night Mode",
            Tag = "Dark vision",
            Gamma = 1.20,
            ShadowBoost = 80.0,
            Brightness = 8.0,
            Contrast = 12.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },

        // Colour pop. The channel gains do the work rather than contrast, so a
        // player model reads as more colourful against the terrain without the
        // whole picture turning harsh.
        new DisplayPreset
        {
            Id = "racing",
            Name = "Vibrant",
            Tag = "High colour",
            Gamma = 1.05,
            ShadowBoost = 15.0,
            Brightness = 0.0,
            Contrast = 15.0,
            RedGain = 1.15,
            GreenGain = 1.12,
            BlueGain = 1.10
        },

        // Anti-haze. Contrast is the point, with a cool bias so smoke and fog stop
        // flattening everything behind them, and gamma pulled under neutral so the
        // extra contrast does not crush the midtones.
        new DisplayPreset
        {
            Id = "rpg",
            Name = "Clarity",
            Tag = "Anti haze",
            Gamma = 0.95,
            ShadowBoost = 20.0,
            Brightness = 0.0,
            Contrast = 30.0,
            RedGain = 0.98,
            GreenGain = 1.00,
            BlueGain = 1.04
        },

        // Long range spotting. The most contrast in the list, to pick a distant
        // edge off the background, with the shadows kept almost flat so a glint
        // has something dark to sit against.
        new DisplayPreset
        {
            Id = "sniper",
            Name = "Snipers",
            Tag = "Long range",
            Gamma = 1.00,
            ShadowBoost = 10.0,
            Brightness = -5.0,
            Contrast = 40.0,
            RedGain = 1.04,
            GreenGain = 1.00,
            BlueGain = 0.96
        },

        // Film. The opposite trade to the competitive presets on purpose: dim,
        // warm, and a firm curve so blacks stay black.
        new DisplayPreset
        {
            Id = "cinematic",
            Name = "Movie Night",
            Tag = "Film",
            Gamma = 0.92,
            ShadowBoost = 5.0,
            Brightness = -15.0,
            Contrast = 20.0,
            RedGain = 1.05,
            GreenGain = 1.00,
            BlueGain = 0.90
        },

        // Animation. Saturated, but the blacks stay off the floor so flat cel
        // shading does not turn into a silhouette.
        new DisplayPreset
        {
            Id = "anime",
            Name = "Anime",
            Tag = "Animation",
            Gamma = 1.02,
            ShadowBoost = 12.0,
            Brightness = 5.0,
            Contrast = 18.0,
            RedGain = 1.12,
            GreenGain = 1.08,
            BlueGain = 1.15
        },

        // Real world daylight. Brightness is the whole point here, to beat glare
        // from a window, so the tone curve is left alone and the output is simply
        // turned up.
        new DisplayPreset
        {
            Id = "daylight",
            Name = "Bright Room",
            Tag = "Daylight",
            Gamma = 1.00,
            ShadowBoost = 10.0,
            Brightness = 30.0,
            Contrast = 15.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },

        // Real world dark room. Pulled right down and warm. The blue light filter
        // that finishes the job is a separate control on the tab, because it is a
        // comfort setting for the room rather than part of a picture preset.
        new DisplayPreset
        {
            Id = "oled",
            Name = "Dark Room",
            Tag = "Low glare",
            Gamma = 0.95,
            ShadowBoost = 0.0,
            Brightness = -35.0,
            Contrast = -10.0,
            RedGain = 1.00,
            GreenGain = 0.95,
            BlueGain = 0.85
        },

        // Paper like. Softened contrast and a warm cast to take the edge off a
        // white background, with enough shadow lift that body text does not turn
        // into a grey smear.
        new DisplayPreset
        {
            Id = "reading",
            Name = "Reading",
            Tag = "Documents",
            Gamma = 1.10,
            ShadowBoost = 20.0,
            Brightness = -20.0,
            Contrast = -12.0,
            RedGain = 1.02,
            GreenGain = 1.02,
            BlueGain = 0.90
        },

        // All day. Nothing dramatic: a slightly soft curve and a touch of warmth,
        // so a long session does not end with tired eyes.
        new DisplayPreset
        {
            Id = "comfort",
            Name = "Comfort",
            Tag = "Easy on eyes",
            Gamma = 0.98,
            ShadowBoost = 5.0,
            Brightness = -12.0,
            Contrast = -15.0,
            RedGain = 0.99,
            GreenGain = 0.99,
            BlueGain = 0.99
        }
    };
}
