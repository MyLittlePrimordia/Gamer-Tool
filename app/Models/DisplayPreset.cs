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

    /// <summary>
    /// Above this, a stored backlight is treated as meaningless.
    /// <para>
    /// Not a clamp - a rejection. The value is the monitor's own scale and the
    /// widest real panels report 100, so anything past a few hundred is a hand-typed
    /// number or a file from something else. Dropping it to "leave the panel alone"
    /// is what a monitor that refused the value would have got anyway, and it beats
    /// clamping 4000000000 down onto a 0-100 scale.
    /// </para>
    /// </summary>
    public const uint MaxPlausibleBacklight = 1000;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// The hardware backlight this preset sets, or null to leave the panel alone.
    /// <para>
    /// Null is the default and the important case. Every preset that existed before
    /// this field deserialises as null, which means "do not touch", so no existing
    /// profile changes behaviour on the day it gains a field - and a preset the user
    /// never gave a backlight keeps behaving exactly as it did.
    /// </para>
    /// <para>
    /// Why it is a uint rather than a percentage: the value that reaches the
    /// hardware is the monitor's own 0-to-maximum scale, which is frequently not
    /// 0-100. Rescaling a preset's number through the wrong denominator is the bug
    /// the brightness slider's own code has a note about, so the raw reading is what
    /// gets stored and the monitor's range is applied at write time.
    /// </para>
    /// <para>
    /// Deliberately not part of the blue-light path. <see cref="WithBlueLight"/>
    /// copies this field untouched, because a night filter warms the picture and
    /// must not also change how bright the panel is.
    /// </para>
    /// </summary>
    public uint? Backlight { get; set; }

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
            Backlight = Backlight,
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

        // Only the null is checked, because the value is a uint already: a JSON
        // number cannot make it negative, and System.Text.Json rejects anything
        // outside uint's range as a parse error rather than truncating it. There is
        // no upper bound to apply here either - it is the monitor's own scale, and
        // the write path clamps it against the range the hardware reported.
        //
        // The one thing worth pulling back is an implausible maximum, which is what
        // a hand-typed 4000000000 on a 0-100 panel looks like. Above any plausible
        // brightness the value is meaningless rather than merely large, so it is
        // dropped to "leave the panel alone" - which is what a monitor that does
        // not answer would get anyway.
        if (Backlight is { } panel && panel > MaxPlausibleBacklight)
        {
            Backlight = null;
        }

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

            // Left null on purpose, and this is the single most important line in
            // the field. Standard means "neutral gamma, panel as it is". If it
            // carried a backlight value then resetting a preset - or hitting the
            // panic key, or quitting a game - would drag every monitor back to a
            // brightness the user set hours ago and had since changed by hand.
            Backlight = null,
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

        // ============ Two rules every entry below obeys ============
        //
        // 1. No channel gain above 1.00.
        //
        //    The ramp is built as Clamp(Curve/peak * gain, 0, 1), so a gain above
        //    one buys saturation by pushing the top of the range past the clamp.
        //    There is no highlight rolloff anywhere in this path, so what a gain
        //    above 1.00 actually buys is white crush - and because the three
        //    channels flatten at *different* input levels, it is not a neutral
        //    crush. It desaturates and hue-shifts as the highlights come up, which
        //    is worse than plain clipping and much harder to see.
        //
        //    So a tint is expressed by anchoring the dominant channel at 1.00 and
        //    pulling the others down. That is strictly darker than boosting, which
        //    is the trade: the image loses a little overall level and keeps its
        //    highlights. Brightness is the knob that gives the level back.
        //
        // 2. Keep the flattening in the highlights small.
        //
        //    Positive contrast is an S-curve toward white, so it inherently eats
        //    the top of the range, and Brightness is a flat additive shift, so
        //    Brightness +N clips the top N% exactly - which is why the old
        //    Bright Room at +30 destroyed thirty percent of its own range.
        //    Gamma is the tool that does not have that problem: it lifts the
        //    midtones and leaves 0 and 1 where they are.
        //
        //    DisplayHighlightClippingTests pins both of these per preset, so a
        //    future tuning that reintroduces either fails the build rather than
        //    shipping.

        // Esports baseline. Most of the work is the shadow lift, so a player
        // separates from a dark background, with gamma just enough to keep the
        // midtones from going muddy.
        //
        // Contrast is negative, and that is load-bearing rather than a taste
        // call. Positive contrast multiplies (value - 0.5), so it pushes the
        // lifted shadows back *down* and cancels the very visibility the shadow
        // term was bought for: at +10 the black point measured 0.231, and at -4
        // with brightness trimmed to +1 it measures 0.2326 - the same lift, with
        // none of the highlight clipping. The old +10 flattened almost a tenth of
        // the range before any channel gain was involved.
        new DisplayPreset
        {
            Id = "camper",
            Name = "Competitive",
            Tag = "Esports",
            Gamma = 1.10,
            ShadowBoost = 60.0,
            Brightness = 1.0,
            Contrast = -4.0,
            RedGain = 0.99,
            GreenGain = 1.00,
            BlueGain = 0.97
        },

        // In-game night vision. This is the heaviest shadow lift in the list on
        // purpose: the whole job is finding someone in an unlit room, which is a
        // different problem from being comfortable in a dark room.
        //
        // Contrast negative for the same reason as Competitive, and the black
        // point sits well above it - lifted to 0.33 deliberately, because a haze
        // you cannot see through is not night vision.
        new DisplayPreset
        {
            Id = "night",
            Name = "Night Mode",
            Tag = "Dark vision",
            Gamma = 1.20,
            ShadowBoost = 80.0,
            Brightness = 4.0,
            Contrast = -4.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },

        // Colour pop, expressed through a warm tint rather than saturation. The
        // channel gains do the work; contrast is held low because this one used to
        // lose almost a fifth of its range in the red channel alone.
        new DisplayPreset
        {
            Id = "racing",
            Name = "Vibrant",
            Tag = "High colour",
            Gamma = 1.02,
            ShadowBoost = 15.0,
            Brightness = 2.0,
            Contrast = 4.0,
            RedGain = 1.00,
            GreenGain = 0.98,
            BlueGain = 0.94
        },

        // Anti-haze. Contrast is the point, with a cool bias so smoke and fog stop
        // flattening everything behind them.
        //
        // Gamma is 1.00 and not the 0.96 this used to be written with, because
        // GammaMin is 1.00 and the authored figure was being silently rewritten on
        // the way in - the preset never had the gamma its comment described. The
        // midtones are protected by the modest contrast instead, which is what the
        // lower gamma was reaching for anyway.
        new DisplayPreset
        {
            Id = "rpg",
            Name = "Clarity",
            Tag = "Anti haze",
            Gamma = 1.00,
            ShadowBoost = 20.0,
            Brightness = 0.0,
            Contrast = 10.0,
            RedGain = 0.97,
            GreenGain = 0.99,
            BlueGain = 1.00
        },

        // Long range spotting. The most contrast in the list that still keeps its
        // highlights - +40 flattened nearly a fifth of the range, which in a game
        // about spotting a distant edge means the edge and the sky above it were
        // the same colour. Shadows stay lifted so a glint has something dark to
        // sit against.
        new DisplayPreset
        {
            Id = "sniper",
            Name = "Snipers",
            Tag = "Long range",
            Gamma = 1.00,
            ShadowBoost = 20.0,
            Brightness = -5.0,
            Contrast = 12.0,
            RedGain = 1.00,
            GreenGain = 0.99,
            BlueGain = 0.96
        },

        // Film. The opposite trade to the competitive presets on purpose: dim,
        // warm, and a firm curve so blacks stay black. The warmest tint in the
        // list, and every one of those three channels is at or below one.
        //
        // Gamma 1.00 for the same reason as Clarity - it was authored at 0.94 and
        // GammaMin silently made that 1.00 anyway, so the number in the file was
        // never the number in use.
        new DisplayPreset
        {
            Id = "cinematic",
            Name = "Movie Night",
            Tag = "Film",
            Gamma = 1.00,
            ShadowBoost = 5.0,
            Brightness = -4.0,
            Contrast = 14.0,
            RedGain = 1.00,
            GreenGain = 0.97,
            BlueGain = 0.90
        },

        // Animation. Cool and saturated, but the blacks stay off the floor so flat
        // cel shading does not turn into a silhouette. Contrast was +18 and lost
        // an eighth of the range across the channels; +6 reads nearly the same and
        // keeps the sky.
        new DisplayPreset
        {
            Id = "anime",
            Name = "Anime",
            Tag = "Animation",
            Gamma = 1.04,
            ShadowBoost = 10.0,
            Brightness = 0.0,
            Contrast = 6.0,
            RedGain = 0.96,
            GreenGain = 0.99,
            BlueGain = 1.00
        },

        // Real world daylight. Brightness is the whole point here, to beat glare
        // from a window - and it is now gamma that delivers it, at +1.20 with the
        // brightness offset gone.
        //
        // This is the entry the first rule above was written for. Brightness was
        // +30 over a gamma of 1.00, and a flat additive shift of that size clips
        // the top thirty percent of the range by construction: it was destroying a
        // third of its own picture to be bright. Gamma lifts the midtones instead,
        // which is where the content is - mid-grey went from 0.50 to 0.56 - while
        // leaving both ends exactly where they were.
        new DisplayPreset
        {
            Id = "daylight",
            Name = "Bright Room",
            Tag = "Daylight",
            Gamma = 1.20,
            ShadowBoost = 10.0,
            Brightness = 0.0,
            Contrast = 0.0,
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
            Gamma = 1.00,
            ShadowBoost = 0.0,
            Brightness = -35.0,
            Contrast = -10.0,
            RedGain = 1.00,
            GreenGain = 0.96,
            BlueGain = 0.88
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
            RedGain = 1.00,
            GreenGain = 0.99,
            BlueGain = 0.92
        },

        // All day. Nothing dramatic: a slightly soft curve and a touch of warmth,
        // so a long session does not end with tired eyes. Gamma was authored at
        // 0.98 and GammaMin silently made that 1.00, so the softness it was
        // reaching for comes from the negative contrast instead, which is the
        // fourth entry to have been written with a figure the model discards.
        new DisplayPreset
        {
            Id = "comfort",
            Name = "Comfort",
            Tag = "Easy on eyes",
            Gamma = 1.00,
            ShadowBoost = 5.0,
            Brightness = -12.0,
            Contrast = -15.0,
            RedGain = 0.99,
            GreenGain = 0.99,
            BlueGain = 0.97
        }
    };
}
