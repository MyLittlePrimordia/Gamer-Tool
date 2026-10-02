using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What the built-in audio presets are allowed to do to a signal that already
/// knows where it is, and how hard they are allowed to push it.
/// <para>
/// Two concerns, both about the same thing. A competitive game outputs binaural
/// audio with real interaural timing, and a second spatialiser on top of it does
/// not widen anything - the two stage models disagree, the sum comb-filters across
/// the midrange, and a footstep as short as a footstep smears off its own direction.
/// Separately, Clarity is a harmonic exciter, so a high setting on gunshot cracks
/// and voice comms is manufactured ear fatigue rather than detail.
/// </para>
/// <para>
/// The headroom assertions are the ones worth reading twice: they are here to pin
/// the fact that the <c>MasterGain</c> field is an offset and not a level. Every
/// preset already sends a negative master, and nothing in this file is allowed to
/// reintroduce a positive one by editing bands without thinking about it.
/// </para>
/// </summary>
public class AudioPresetAcousticTests
{
    /// <summary>
    /// Ceiling for every built-in. Late Night sits exactly on it and is meant to:
    /// it is the one preset whose material is quiet and mostly speech.
    /// </summary>
    private const double MaxClarity = 4.5;

    private static AudioPreset Preset(string id) =>
        AudioPreset.Defaults.First(p => p.Id == id);

    // ---- HRTF protection ----

    [Theory]
    [InlineData("footstep")]
    [InlineData("royale")]
    public void A_competitive_preset_adds_no_spatialiser(string id)
    {
        // The engines that need this are Counter-Strike, Valorant, Apex and
        // Warzone: all of them mix for headphones and all of them already place
        // things. Ambience and surround on top of a correct binaural master is not
        // a wider image, it is a smeared one.
        AudioPreset preset = Preset(id);

        Assert.Equal(0.0, preset.Ambience);
        Assert.Equal(0.0, preset.Surround);
    }

    [Theory]
    [InlineData("explosion")]
    [InlineData("racing")]
    [InlineData("arcade")]
    [InlineData("cinematic")]
    public void A_stage_preset_keeps_its_spatial_cues(string id)
    {
        // The other side of the same decision, and it is a decision rather than a
        // default. These target games that mix for a room, so there is no binaural
        // master to duplicate and the cues are carrying information.
        AudioPreset preset = Preset(id);

        Assert.True(preset.Ambience > 0.0, id + " lost its ambience");
        Assert.True(preset.Surround > 0.0, id + " lost its surround");
    }

    [Fact]
    public void Only_the_two_competitive_presets_lost_their_spatialiser()
    {
        // Pinned as a set rather than a lookup, because "the two competitive ones"
        // is a claim about the whole list. Three of these were already silent for
        // their own reasons - Flat is the untouched reference, Voice Chat is
        // comms, Late Night is quiet - so five are silent now and only two of them
        // were changed by this pass. The count is what stops a sixth appearing.
        List<string> silent = AudioPreset.Defaults
            .Where(p => p.Ambience == 0.0 && p.Surround == 0.0)
            .Select(p => p.Id)
            .ToList();

        Assert.Equal(5, silent.Count);
        Assert.Contains("footstep", silent);
        Assert.Contains("royale", silent);
        Assert.Contains("flat", silent);
        Assert.Contains("moba", silent);
        Assert.Contains("latenight", silent);
    }

    // ---- Anti-sibilance ----

    [Fact]
    public void No_built_in_exciter_goes_past_the_cap()
    {
        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            Assert.True(
                preset.Clarity <= MaxClarity,
                preset.Id + " is at clarity " + preset.Clarity + ", above the cap of " + MaxClarity);
        }
    }

    [Fact]
    public void The_voice_chat_preset_does_not_excite_sibilance()
    {
        // The worst placement for an exciter, and it was the worst offender: this
        // is the preset people leave on while they are on comms, so it is the one
        // where added harmonic energy in the sibilant band is heard for hours.
        AudioPreset moba = Preset("moba");

        Assert.Equal(4.0, moba.Clarity);
    }

    [Fact]
    public void The_combat_preset_does_not_excite_gunshot_cracks()
    {
        // Not on the list to change, and it had to be: Shooter had the highest
        // clarity of the three combat curves and sat above the cap the others were
        // pulled under. A gunshot crack is the loudest thing in the game it is
        // aimed at.
        AudioPreset shooter = Preset("explosion");

        Assert.True(shooter.Clarity <= MaxClarity);
        Assert.Equal(4.0, shooter.Clarity);
    }

    [Fact]
    public void A_preset_at_the_cap_is_at_the_cap_on_purpose()
    {
        // Late Night is the one defensible exception and it is held to the number
        // rather than waved through, so raising the cap later has to be a decision
        // someone makes.
        Assert.Equal(MaxClarity, Preset("latenight").Clarity);
    }

    // ---- Headroom ----

    /// <summary>
    /// Every built-in sends a master gain of zero or less once the anti-clip
    /// offset has been taken off.
    /// <para>
    /// This is the assertion that would catch the mistake that is easiest to make
    /// here. <c>MasterGain</c> is not the level the engine gets - it is an offset
    /// from which <c>RequiredHeadroom</c>, the largest positive band boost plus
    /// half a decibel, is subtracted before the command is built. Footsteps reads
    /// +3.0 in the source and sends -3.5. So a preset whose bands are edited
    /// without regard for this can still ship a clipping curve as long as nobody
    /// looks at the master field, and reading that field is exactly what a tuning
    /// change invites.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_built_in_sends_a_master_gain_of_zero_or_less()
    {
        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            double sent = AudioService.EffectiveMasterGain(preset, antiClip: true);

            Assert.True(
                sent <= 0.0,
                preset.Id + " would send " + sent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " dB of master gain; the headroom offset is supposed to absorb the largest boost");
        }
    }

    [Fact]
    public void The_headroom_offset_is_the_largest_boost_plus_a_margin()
    {
        // Pinned so the relationship between the bands and the master cannot drift
        // quietly. If this ever stops being true, every assertion above is passing
        // for the wrong reason.
        AudioPreset footstep = Preset("footstep");

        double largest = footstep.Bands.Max();
        double expected = Math.Min(AudioPreset.GainMax, largest + 0.5);

        Assert.Equal(6.0, largest, 3);
        Assert.Equal(expected, AudioService.RequiredHeadroom(footstep), 4);
    }

    [Fact]
    public void A_positive_master_in_the_source_is_still_sent_negative()
    {
        // The specific trap, as its own test. Three of the built-ins carry a
        // positive MasterGain, and that looks alarming until you know what it is
        // for. If someone "fixes" one of them to be negative the result is a
        // preset six decibels quieter than intended, with no error anywhere.
        List<string> positive = AudioPreset.Defaults
            .Where(p => p.MasterGain > 0.0)
            .Select(p => p.Id)
            .ToList();

        Assert.Contains("footstep", positive);
        Assert.Contains("royale", positive);
        Assert.Contains("explosion", positive);

        foreach (AudioPreset preset in AudioPreset.Defaults.Where(p => p.MasterGain > 0.0))
        {
            Assert.True(
                AudioService.EffectiveMasterGain(preset, antiClip: true) < 0.0,
                preset.Id + " carries a positive offset and should still send negative");
        }
    }

    [Fact]
    public void Turning_anti_clip_off_is_the_only_way_to_send_a_positive_master()
    {
        // And that is the point of it being a switch. Confirms the two paths really
        // do differ, so the assertions above are testing the protection rather than
        // a preset field that happens to be negative.
        AudioPreset footstep = Preset("footstep");

        Assert.True(AudioService.EffectiveMasterGain(footstep, antiClip: false) > 0.0);
        Assert.True(AudioService.EffectiveMasterGain(footstep, antiClip: true) < 0.0);
    }

    // ---- Shape ----

    [Fact]
    public void Every_built_in_has_the_band_count_it_claims()
    {
        // RequiredHeadroom reads the array through NumBands rather than trusting
        // its length, so an array that is longer than the count it declares folds
        // a band nobody can hear into the master offset and the preset comes out
        // quieter than written.
        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            Assert.Equal(AudioPreset.PresetBandCount, preset.NumBands);
            Assert.Equal(AudioPreset.PresetBandCount, preset.Bands.Length);
        }
    }

    [Fact]
    public void Every_built_in_stays_inside_the_models_own_limits()
    {
        // Clamp rewrites rather than refuses, so an authored value outside the
        // limits runs silently and the file stops describing the tune. Four of the
        // display presets were found that way.
        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            AudioPreset copy = preset.Copy();
            copy.Clamp();

            for (int i = 0; i < copy.Bands.Length; i++)
            {
                Assert.Equal(preset.Bands[i], copy.Bands[i], 4);
            }

            Assert.Equal(preset.Clarity, copy.Clarity, 4);
            Assert.Equal(preset.Ambience, copy.Ambience, 4);
            Assert.Equal(preset.Surround, copy.Surround, 4);
            Assert.Equal(preset.DynamicBoost, copy.DynamicBoost, 4);
            Assert.Equal(preset.BassBoost, copy.BassBoost, 4);
            Assert.Equal(preset.MasterGain, copy.MasterGain, 4);
            Assert.Equal(preset.VolumeLeveling, copy.VolumeLeveling, 4);
            Assert.Equal(preset.FilterQ, copy.FilterQ, 4);
            Assert.Equal(preset.Balance, copy.Balance, 4);
        }
    }

    [Fact]
    public void The_levelling_values_were_left_alone()
    {
        // Explicitly not part of this pass: the levelling figures are unchanged,
        // and the loud guard's floor of three still governs any preset below it when
        // the guard is on. Pinned so the next tuning pass does not quietly take
        // them as already done.
        Assert.Equal(3.0, Preset("footstep").VolumeLeveling);
        Assert.Equal(3.5, Preset("royale").VolumeLeveling);
        Assert.Equal(4.0, Preset("moba").VolumeLeveling);
        Assert.Equal(4.0, Preset("latenight").VolumeLeveling);
    }

    [Fact]
    public void The_master_offsets_were_left_alone()
    {
        // Likewise. These are offsets from which headroom is subtracted, so they
        // are not levels and reducing them would be a volume cut nobody asked for.
        Assert.Equal(3.0, Preset("footstep").MasterGain);
        Assert.Equal(2.0, Preset("royale").MasterGain);
        Assert.Equal(1.0, Preset("explosion").MasterGain);
        Assert.Equal(-3.0, Preset("basshead").MasterGain);
    }

    [Fact]
    public void The_legacy_ids_are_all_still_there()
    {
        string[] expected =
        {
            "flat", "footstep", "royale", "explosion", "racing", "arcade",
            "moba", "basshead", "soundtrack", "lofi", "cinematic", "latenight",
        };

        List<string> actual = AudioPreset.Defaults.Select(p => p.Id).ToList();

        foreach (string id in expected)
        {
            Assert.Contains(id, actual);
        }

        Assert.Equal(expected.Length, actual.Count);
    }

    [Fact]
    public void The_competitive_presets_still_cut_the_explosions()
    {
        // The thing the HRTF work must not have cost them. Both presets exist to
        // keep low-frequency explosion energy out of the way of a footstep, and
        // their new curves are smoother through the sub-bass than the ones they
        // replace - so this checks the cut is still there and not merely gentler.
        foreach (string id in new[] { "footstep", "royale" })
        {
            AudioPreset preset = Preset(id);

            Assert.True(preset.Band(0) < 0.0, id + " no longer cuts the lowest band");
            Assert.True(preset.Band(1) < 0.0, id + " no longer cuts the second band");

            // And the presence bands still come up, or there is nothing left of the
            // preset: the loudest bands are in the middle, where scuffs and
            // reloads live.
            Assert.True(preset.Band(6) > 0.0, id + " lost its presence peak");
        }
    }
}