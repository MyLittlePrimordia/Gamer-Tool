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

    public string ShadowText => "+" + ShadowBoost.ToString("0") + "%";

    public string BrightnessText => (Brightness >= 0 ? "+" : string.Empty) + Brightness.ToString("0") + "%";

    public string ContrastText => (Contrast >= 0 ? "+" : string.Empty) + Contrast.ToString("0") + "%";

    public string GammaText => Gamma.ToString("0.00");

    public string RgbText => RedGain.ToString("0.00") + " " + GreenGain.ToString("0.00") + " " + BlueGain.ToString("0.00");

    public string SpecText => "SHADOW " + ShadowText + "   BRIGHT " + BrightnessText + "   CONTRAST " + ContrastText;

    public string CompactSpec => "SHADOW " + ShadowText + "  ·  GAMMA " + GammaText;

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

    public static DisplayPreset Flat()
    {
        return new DisplayPreset
        {
            Id = "flat",
            Name = "STANDARD",
            Tag = "NO CHANGE",
            Gamma = 1.00,
            ShadowBoost = 0.0,
            Brightness = 0.0,
            Contrast = 0.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        };
    }

    // Values follow common competitive OSD practice: gamma is a ramp multiplier
    // (1.00 = untouched), so the "look" comes from shadow boost (pro black
    // equalizer) plus a slightly negative contrast rather than a heavy gamma lift.
    public static IReadOnlyList<DisplayPreset> Defaults { get; } = new List<DisplayPreset>
    {
        new DisplayPreset
        {
            Id = "camper",
            Name = "COMPETITIVE FPS",
            Tag = "PRO LOOK",
            Gamma = 1.10,
            ShadowBoost = 55.0,
            Brightness = 5.0,
            Contrast = -5.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },
        new DisplayPreset
        {
            Id = "daylight",
            Name = "DAYLIGHT",
            Tag = "BRIGHT ROOM",
            Gamma = 1.00,
            ShadowBoost = 0.0,
            Brightness = 15.0,
            Contrast = -5.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },
        new DisplayPreset
        {
            Id = "vibrant",
            Name = "VIBRANT",
            Tag = "RICH COLOR",
            Gamma = 1.05,
            ShadowBoost = 15.0,
            Brightness = 5.0,
            Contrast = 10.0,
            RedGain = 1.04,
            GreenGain = 1.00,
            BlueGain = 1.05
        },
        new DisplayPreset
        {
            Id = "sniper",
            Name = "ESPORTS",
            Tag = "MAX DETAIL",
            Gamma = 1.12,
            ShadowBoost = 75.0,
            Brightness = 8.0,
            Contrast = -8.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },
        new DisplayPreset
        {
            Id = "night",
            Name = "DARK ROOM",
            Tag = "LATE NIGHT",
            Gamma = 1.15,
            ShadowBoost = 35.0,
            Brightness = -20.0,
            Contrast = 0.0,
            RedGain = 1.00,
            GreenGain = 0.98,
            BlueGain = 0.94
        },
        new DisplayPreset
        {
            Id = "cinematic",
            Name = "CINEMA",
            Tag = "SOFT WARM",
            Gamma = 1.08,
            ShadowBoost = 20.0,
            Brightness = -5.0,
            Contrast = 5.0,
            RedGain = 1.02,
            GreenGain = 1.00,
            BlueGain = 0.97
        },
        new DisplayPreset
        {
            Id = "oled",
            Name = "READING / TEXT",
            Tag = "CRISP UI",
            Gamma = 1.05,
            ShadowBoost = 45.0,
            Brightness = 5.0,
            Contrast = 5.0,
            RedGain = 1.00,
            GreenGain = 1.00,
            BlueGain = 1.00
        },
        new DisplayPreset
        {
            Id = "retro",
            Name = "RETRO ARCADE",
            Tag = "OLD TV",
            Gamma = 1.00,
            ShadowBoost = 30.0,
            Brightness = 5.0,
            Contrast = 15.0,
            RedGain = 0.95,
            GreenGain = 1.05,
            BlueGain = 0.95
        }
    };
}
