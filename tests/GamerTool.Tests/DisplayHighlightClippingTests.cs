using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What the built-in display presets do to the top of the range.
/// <para>
/// The defect these exist to prevent is not visible in the numbers anyone reads and
/// is only obvious once the ramp is built. The ramp is
/// <c>Clamp(Curve/peak * gain, 0, 1)</c> with no highlight rolloff anywhere in the
/// path, so a channel gain above 1.00 does not add saturation - it pushes the top of
/// the range past a hard clamp. And because the three channels flatten at different
/// input levels, that is not a neutral white crush: the highlights desaturate and
/// hue-shift on their way up, which is both worse and much harder to notice.
/// </para>
/// <para>
/// Measured, the shipping Vibrant preset was flattening a fifth of the input range in
/// blue and a sixth in red, and Bright Room was destroying thirty percent of its own
/// picture because a brightness offset of +30 over a gamma of 1.00 clips the top
/// thirty percent by construction.
/// </para>
/// <para>
/// Two invariants, pinned per preset so a future tuning that reintroduces either
/// fails the build rather than shipping.
/// </para>
/// </summary>
public class DisplayHighlightClippingTests
{
    /// <summary>
    /// Ceiling for every built-in. The worst one now is Clarity at a little over
    /// four percent, which is what a deliberate anti-haze curve costs.
    /// </summary>
    private const double MaxFlattening = 0.06;

    private static double[] Flatten(DisplayPreset preset) =>
        DisplayService.HighlightFlattening(preset);

    private static double Worst(DisplayPreset preset) => Flatten(preset).Max();

    // ---- Rule 1: no channel gain above 1.00 ----

    [Fact]
    public void No_built_in_boosts_a_channel_above_one()
    {
        // The hard rule. A gain above one is the only thing in this path that can
        // flatten a channel on its own, and the three are flattened at different
        // input levels, so the result is progressive desaturation rather than a
        // clean white point.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            Assert.True(preset.RedGain <= 1.00, preset.Id + " boosts red to " + preset.RedGain);
            Assert.True(preset.GreenGain <= 1.00, preset.Id + " boosts green to " + preset.GreenGain);
            Assert.True(preset.BlueGain <= 1.00, preset.Id + " boosts blue to " + preset.BlueGain);
        }
    }

    [Fact]
    public void A_gain_above_one_still_clips_and_the_test_can_see_it()
    {
        // The pin is only worth anything if it fails when it should. A synthetic
        // preset in the shape the shipped Vibrant used to have, measured against
        // the same seam, so this is a control rather than a restatement.
        var loud = new DisplayPreset
        {
            Id = "probe",
            Name = "Probe",
            Gamma = 1.05,
            ShadowBoost = 15.0,
            Brightness = 0.0,
            Contrast = 15.0,
            RedGain = 1.15,
            GreenGain = 1.12,
            BlueGain = 1.10,
        };

        Assert.True(Worst(loud) > MaxFlattening);
    }

    [Fact]
    public void A_tint_expressed_by_pulling_channels_down_does_not_clip()
    {
        // The other half of the trade, so the rule above is not read as "no colour".
        // Anchoring the dominant channel at one and pulling the others down gives
        // the same direction of tint with no gain above one.
        var warm = new DisplayPreset
        {
            Id = "probe",
            Name = "Probe",
            Gamma = 1.02,
            ShadowBoost = 15.0,
            Brightness = 2.0,
            Contrast = 4.0,
            RedGain = 1.00,
            GreenGain = 0.98,
            BlueGain = 0.94,
        };

        Assert.True(Worst(warm) <= MaxFlattening);
        Assert.True(warm.RedGain > warm.BlueGain, "a warm tint should still read warm");
    }

    // ---- Rule 2: keep the flattening small ----

    [Fact]
    public void No_built_in_flattens_more_than_the_ceiling()
    {
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            double[] flat = Flatten(preset);
            string detail = string.Join(
                " ",
                new[] { "red", "green", "blue" }.Select((name, i) =>
                    name + "=" + (flat[i] * 100).ToString("0.0",
                        System.Globalization.CultureInfo.InvariantCulture) + "%"));

            Assert.True(Worst(preset) <= MaxFlattening, preset.Id + " flattens " + detail);
        }
    }

    [Theory]
    [InlineData("racing", 0.05)]
    [InlineData("anime", 0.05)]
    [InlineData("sniper", 0.03)]
    [InlineData("camper", 0.01)]
    [InlineData("daylight", 0.01)]
    public void The_presets_that_used_to_clip_are_pinned_individually(string id, double ceiling)
    {
        // Tighter than the blanket ceiling, because these are the ones that were
        // measured losing a sixth to a third of their range, and a single shared
        // number would let one improve at another's expense.
        DisplayPreset preset = DisplayPreset.Defaults.First(p => p.Id == id);

        Assert.True(Worst(preset) <= ceiling, id + " flattens " + (Worst(preset) * 100).ToString("0.0",
            System.Globalization.CultureInfo.InvariantCulture) + "%");
    }

    [Fact]
    public void The_neutral_preset_flattens_nothing()
    {
        Assert.Equal(0.0, Worst(DisplayPreset.Flat()), 6);
    }

    [Fact]
    public void Competitive_keeps_the_black_lift_and_loses_the_clipping()
    {
        // The specific claim the retuning made. The old preset reached a black point
        // of 0.231 by spending +10 contrast, which flattened almost a tenth of the
        // range on the way - positive contrast multiplies (value - 0.5), so it
        // pushes the lifted shadows back down and cancels the visibility the shadow
        // term was bought for.
        //
        // Negative contrast holds the same lift without reaching the clamp, because
        // it pulls the top of the curve down instead of pushing it up. The offset is
        // trimmed to +1 to land on the original figure rather than overshoot it.
        DisplayPreset camper = DisplayPreset.Defaults.First(p => p.Id == "camper");

        Assert.Equal(60.0, camper.ShadowBoost, 3);
        Assert.True(camper.Contrast < 0.0, "competitive should not spend contrast it cannot afford");
        Assert.Equal(0.0, Worst(camper), 6);
    }

    [Fact]
    public void Bright_room_gets_its_brightness_from_gamma_now()
    {
        // A brightness offset over a gamma of 1.00 is a flat additive shift, and a
        // shift of +N clips the top N% of the range exactly - which is what made the
        // old Bright Room throw away a third of its own picture to be bright.
        // Gamma lifts the midtones and leaves both ends alone.
        DisplayPreset daylight = DisplayPreset.Defaults.First(p => p.Id == "daylight");

        Assert.Equal(0.0, Worst(daylight), 6);
        Assert.True(daylight.Gamma > 1.0, "bright room should be lifting the midtones, not offsetting the curve");
        Assert.True(Math.Abs(daylight.Brightness) < 1.0, "a large brightness offset clips by construction");
    }

    // ---- Stable identity, and the panel left alone ----

    [Fact]
    public void The_legacy_ids_are_all_still_there()
    {
        // Saved profiles and backups reference presets by id, and BackupService
        // repairs against this list, so a renamed or dropped id orphans a slot
        // rather than migrating it.
        string[] expected =
        {
            "flat", "camper", "night", "racing", "rpg", "sniper",
            "cinematic", "anime", "daylight", "oled", "reading", "comfort",
        };

        List<string> actual = DisplayPreset.Defaults.Select(p => p.Id).ToList();

        foreach (string id in expected)
        {
            Assert.Contains(id, actual);
        }

        Assert.Equal(expected.Length, actual.Count);
    }

    [Fact]
    public void No_built_in_claims_the_hardware_backlight()
    {
        // Deliberate, and worth pinning. Backlight is the monitor's own 0-to-maximum
        // scale rather than a percentage, so a value baked into a shipped preset
        // would clamp to whatever that particular panel's maximum is - which on the
        // common 0-100 panel means every one of them would set full brightness, and
        // a dark room preset would brighten the room.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            Assert.Null(preset.Backlight);
        }
    }

    [Fact]
    public void Every_built_in_survives_its_own_clamp_unchanged()
    {
        // The values are authored inside the model's own limits, so Clamp has nothing
        // to do. This catches an entry whose gains fall outside ChannelGainMin or
        // Max, which would otherwise be silently rewritten rather than refused - the
        // reason the earlier monochrome proposal came back as a flat desaturation.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            DisplayPreset copy = preset.Copy();
            copy.Clamp();

            Assert.Equal(preset.RedGain, copy.RedGain, 4);
            Assert.Equal(preset.GreenGain, copy.GreenGain, 4);
            Assert.Equal(preset.BlueGain, copy.BlueGain, 4);
            Assert.Equal(preset.Contrast, copy.Contrast, 4);
            Assert.Equal(preset.Gamma, copy.Gamma, 4);
        }
    }

    [Fact]
    public void No_built_in_is_authored_below_the_gamma_floor()
    {
        // Four of the shipped presets were written with a gamma under 1.00 - Clarity
        // 0.96, Movie Night 0.94, Dark Room 0.95, Comfort 0.98 - and GammaMin is
        // 1.00, so Clamp silently rewrote every one of them on the way in. Each was
        // running at 1.00 while its own comment described a curve pushed under
        // neutral, which is the worst kind of drift: the file says one thing and the
        // screen does another, and nothing reports it.
        //
        // Named separately because the general clamp test above only catches it as a
        // symptom. This one says what it is.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            Assert.True(
                preset.Gamma >= DisplayPreset.GammaMin,
                preset.Id + " is authored at gamma " + preset.Gamma + ", below GammaMin "
                    + DisplayPreset.GammaMin + ", so the value in the file is not the value in use");
        }
    }

    [Fact]
    public void A_tint_is_reachable_within_the_gains_the_model_allows()
    {
        // The monochrome proposal was RGB (0.30, 0.59, 0.11), which Clamp rewrites
        // to (0.70, 0.70, 0.70) - a flat thirty percent desaturation, not the green
        // the numbers looked like. Pinned so the ceiling is a known quantity.
        DisplayPreset probe = new() { RedGain = 0.30, GreenGain = 0.59, BlueGain = 0.11 };

        probe.Clamp();

        Assert.Equal(DisplayPreset.ChannelGainMin, probe.RedGain, 4);
        Assert.Equal(DisplayPreset.ChannelGainMin, probe.BlueGain, 4);
        Assert.Equal(DisplayPreset.ChannelGainMin, probe.RedGain, 4);
    }
}