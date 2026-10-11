using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A hotkey has to mean exactly one thing no matter how it was written, because
/// the duplicate check, the Windows registration and the on-screen keycap all
/// have to agree or a binding silently fails.
/// </summary>
public class HotkeyTests
{
    [Theory]
    [InlineData("CTRL+1", "ctrl+alt+1")]              // different combos
    [InlineData("CTRL+1", "ALT+CTRL+1")]
    [InlineData("CTRL+SHIFT+1", "CTRL+1")]            // different modifiers
    [InlineData("CTRL+1", "ALT+1")]
    [InlineData("A", "B")]
    [InlineData("NUM1", "1")]                        // numpad is not the number row
    [InlineData("F1", "F2")]
    public void Different_bindings_stay_different(string left, string right)
    {
        Assert.NotEqual(SlotService.HotkeyKey(left), SlotService.HotkeyKey(right));
    }

    [Theory]
    [InlineData("CTRL+1", "ctrl+1")]                  // case
    [InlineData("CTRL+1", "CTRL + 1")]                // spacing
    [InlineData("CTRL+1", "CONTROL+1")]               // alias
    [InlineData("WIN+A", "WINDOWS+A")]                // alias
    [InlineData("ALT+CTRL+1", "CTRL+ALT+1")]           // order
    [InlineData("num1", "NUM1")]
    public void The_same_binding_always_reduces_to_one_key(string left, string right)
    {
        Assert.Equal(SlotService.HotkeyKey(left), SlotService.HotkeyKey(right));
    }

    [Fact]
    public void The_capture_key_and_the_registration_key_agree()
    {
        // The capture handler writes this shape; a restored backup can hold any
        // shape at all. Both have to reduce to the same thing or a duplicate
        // slips past the check and then fails to register.
        string captured = HotkeyService.FromInput(System.Windows.Input.Key.D1, HotkeyModifiers.Control);
        string fromBackup = "control + 1";

        Assert.Equal(SlotService.HotkeyKey(captured), SlotService.HotkeyKey(fromBackup));
    }

    [Fact]
    public void An_unparseable_binding_still_reduces_to_something_stable()
    {
        Assert.Equal(SlotService.HotkeyKey("not a key"), SlotService.HotkeyKey("NOT A KEY"));
    }

    [Theory]
    [InlineData("CTRL+1", true)]
    [InlineData("ALT+CTRL+SHIFT+1", true)]
    [InlineData("F12", true)]
    [InlineData("NUM5", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("CTRL+", false)]
    [InlineData("BANANA+1", false)]
    public void Binding_parsing_agrees_with_itself(string text, bool expected)
    {
        Assert.Equal(expected, HotkeyService.TryParse(text, out _, out System.Windows.Input.Key key) && key != System.Windows.Input.Key.None);
    }

    [Fact]
    public void Modifiers_round_trip_through_a_canonical_form()
    {
        HotkeyService.TryParse("alt+control+shift+1", out HotkeyModifiers mods, out _);

        string canonical = HotkeyService.Normalise("alt+control+shift+1");

        Assert.True(mods.HasFlag(HotkeyModifiers.Control));
        Assert.True(mods.HasFlag(HotkeyModifiers.Alt));
        Assert.True(mods.HasFlag(HotkeyModifiers.Shift));
        Assert.Equal("CTRL+ALT+SHIFT+1", canonical);
    }
}

/// <summary>
/// The equaliser maths. These numbers decide what actually reaches the ears and
/// what the monitor is told, so they are pinned rather than trusted.
/// </summary>
public class AudioPresetTests
{
    [Fact]
    public void Headroom_matches_the_largest_boost_plus_a_margin()
    {
        AudioPreset preset = AudioPreset.Flat();
        preset.Bands[5] = 6.0;

        Assert.Equal(6.5, AudioService.RequiredHeadroom(preset), 3);
    }

    [Fact]
    public void No_boost_means_no_headroom()
    {
        Assert.Equal(0.0, AudioService.RequiredHeadroom(AudioPreset.Flat()));
    }

    [Fact]
    public void A_cut_does_not_add_headroom()
    {
        AudioPreset preset = AudioPreset.Flat();
        preset.Bands[0] = -12.0;

        Assert.Equal(0.0, AudioService.RequiredHeadroom(preset));
    }

    [Fact]
    public void Anti_clip_trims_the_preamp_by_the_headroom()
    {
        AudioPreset preset = AudioPreset.Flat();
        preset.MasterGain = 6.0;
        preset.Bands[5] = 6.0;

        // 6.0 gain, 6.5 dB of headroom asked back, and the result still lands
        // inside the master's own range.
        Assert.Equal(6.0, AudioService.EffectiveMasterGain(preset, antiClip: false), 3);
        Assert.Equal(-0.5, AudioService.EffectiveMasterGain(preset, antiClip: true), 3);
    }

    [Fact]
    public void Anti_clip_never_pushes_the_master_outside_its_range()
    {
        AudioPreset preset = AudioPreset.Flat();
        preset.MasterGain = -20.0;
        preset.Bands[5] = 12.0;

        Assert.InRange(AudioService.EffectiveMasterGain(preset, antiClip: true), AudioPreset.MasterGainMin, AudioPreset.MasterGainMax);
    }

    [Fact]
    public void A_copy_does_not_share_the_band_array_with_its_original()
    {
        AudioPreset original = AudioPreset.Flat();
        AudioPreset copy = original.Copy();

        copy.Bands[0] = 5.0;
        copy.Frequencies = new[] { 1.0 };

        Assert.Equal(0.0, original.Bands[0]);
        Assert.Empty(original.Frequencies);
    }

    [Fact]
    public void A_copy_is_always_long_enough_to_edit()
    {
        AudioPreset tiny = new() { Id = "x", Name = "X", NumBands = 10, Bands = Array.Empty<double>() };

        Assert.True(tiny.Copy().Bands.Length >= AudioPreset.PresetBandCount);
    }

    [Fact]
    public void Every_shipped_preset_has_a_distinct_id()
    {
        List<string> ids = AudioPreset.Defaults.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_shipped_audio_preset_keeps_its_band_count_in_step_with_its_array()
    {
        // This is the invariant the preamp depends on: RequiredHeadroom walks
        // Bands.Length while the apply walks NumBands, so a mismatch between
        // them would quietly under-apply the headroom.
        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            Assert.Equal(preset.NumBands, preset.Bands.Length);
        }
    }

    [Fact]
    public void Every_shipped_band_count_has_a_known_frequency_table()
    {
        foreach (int count in AudioPreset.BandCounts)
        {
            Assert.True(AudioPreset.BandFrequencies.ContainsKey(count), count + " has no table");
            Assert.Equal(count, AudioPreset.BandFrequencies[count].Length);
        }
    }

    [Fact]
    public void Only_the_band_counts_with_real_dials_report_one()
    {
        Assert.True(AudioPreset.HasFrequencyDial(5));
        Assert.True(AudioPreset.HasFrequencyDial(10));
        Assert.False(AudioPreset.HasFrequencyDial(15));
        Assert.False(AudioPreset.HasFrequencyDial(20));
        Assert.False(AudioPreset.HasFrequencyDial(31));
    }

    [Fact]
    public void Band_windows_are_ordered_and_touch_without_crossing()
    {
        // The windows are read off the engine's own dial ranges, so they are
        // adjacent rather than separated: at 5 bands, band 1 tops out at 125 Hz
        // and band 2 starts there. Two bands can therefore both be parked on
        // 125 Hz, which the engine tolerates and the app cannot change without
        // invalidating every stored frequency. What must hold is that they never
        // cross, because a crossing band is one that can sit inside its
        // neighbour's range and the two then fight for the same part of the
        // spectrum.
        foreach (int count in AudioPreset.BandCounts)
        {
            if (!AudioPreset.BandWindows.TryGetValue(count, out AudioPreset.BandWindow[]? windows))
            {
                continue;
            }

            for (int i = 0; i < windows.Length; i++)
            {
                Assert.True(windows[i].Max >= windows[i].Min, count + " band " + (i + 1) + " has an empty window");

                if (i > 0)
                {
                    Assert.True(
                        windows[i].Min >= windows[i - 1].Max,
                        count + " band " + (i + 1) + " starts at " + windows[i].Min + ", below band " + i + "'s top of " + windows[i - 1].Max);
                }
            }
        }
    }

    [Fact]
    public void A_dial_always_lands_on_a_whole_hertz_inside_its_window()
    {
        foreach (int count in AudioPreset.BandCounts)
        {
            if (!AudioPreset.BandWindows.TryGetValue(count, out AudioPreset.BandWindow[]? windows))
            {
                continue;
            }

            int steps = AudioPreset.Steps(windows[0]);
            for (int step = 0; step <= steps; step++)
            {
                double hz = AudioPreset.FrequencyFromStep(windows[0], step);

                Assert.Equal(Math.Round(hz), hz);
                Assert.InRange(hz, windows[0].Min, windows[0].Max);
            }
        }
    }

    [Fact]
    public void A_stored_frequency_maps_back_to_the_dial_position_it_came_from()
    {
        AudioPreset.BandWindow window = AudioPreset.Window(10, 0);

        double step = AudioPreset.StepFromFrequency(window, 70.0);
        double hz = AudioPreset.FrequencyFromStep(window, step);

        Assert.Equal(70.0, hz, 0);
    }

    [Fact]
    public void Anti_clip_headroom_is_not_computed_past_the_band_count()
    {
        // A preset whose array is longer than its band count must still only
        // consider the bands that will actually be sent.
        AudioPreset preset = new()
        {
            Id = "x",
            Name = "X",
            NumBands = 10,
            Bands = new double[10]
        };

        Assert.Equal(0.0, AudioService.RequiredHeadroom(preset));

        preset.Bands[9] = 4.0;
        Assert.Equal(4.5, AudioService.RequiredHeadroom(preset), 3);
    }

    [Theory]
    [InlineData(62.0, "62")]
    [InlineData(4666.0, "4.7K")]
    [InlineData(2520.0, "2.5K")]
    [InlineData(1361.0, "1.4K")]
    [InlineData(16000.0, "16K")]
    [InlineData(20000.0, "20K")]
    public void A_frequency_label_carries_a_consistent_precision(double hz, string expected)
    {
        Assert.Equal(expected, AudioPreset.FormatFrequency(hz));
    }

    [Fact]
    public void Every_band_label_on_the_default_strip_uses_the_same_shape()
    {
        // One row of ten labels should not read as two different kinds of number:
        // whole hertz up to a kilohertz, then one decimal place, then whole
        // kilohertz, with nothing carrying more precision than the band grid has.
        string[] labels = Enumerable.Range(0, 10)
            .Select(i => AudioPreset.FormatFrequency(AudioPreset.BandFrequency(10, i)))
            .ToArray();

        Assert.Equal(
            new[] { "62", "116", "214", "397", "735", "1.4K", "2.5K", "4.7K", "8.6K", "16K" },
            labels);
    }
}

/// <summary>
/// The display maths, including the ramp that is actually pushed to the screen.
/// </summary>
public class DisplayPresetTests
{
    [Fact]
    public void A_flat_preset_produces_a_straight_identity_ramp()
    {
        byte[] lut = DisplayService.BuildPreviewLut(DisplayPreset.Flat());

        Assert.Equal(256 * 3, lut.Length);
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal((byte)i, lut[(i * 3) + 0]);
            Assert.Equal((byte)i, lut[(i * 3) + 1]);
            Assert.Equal((byte)i, lut[(i * 3) + 2]);
        }
    }

    [Fact]
    public void Every_shipped_preset_produces_a_monotonic_ramp()
    {
        // A non-monotonic ramp inverts tones somewhere, which reads as a picture
        // that cannot be fixed by turning a knob.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            byte[] lut = DisplayService.BuildPreviewLut(preset);

            for (int i = 1; i < 256; i++)
            {
                Assert.True(
                    lut[(i * 3)] >= lut[((i - 1) * 3)],
                    preset.Id + " red falls between " + (i - 1) + " and " + i);
            }
        }
    }

    [Fact]
    public void A_preset_with_nonsense_values_still_produces_a_legal_ramp()
    {
        // This is the shape a hand-edited settings file can arrive in, and the
        // ramp must stay inside 0..255 whatever it is handed.
        DisplayPreset broken = new()
        {
            Id = "broken",
            Name = "Broken",
            Gamma = 1000,
            ShadowBoost = 100000,
            Brightness = -100000,
            Contrast = 100000,
            RedGain = 50,
            GreenGain = 50,
            BlueGain = 50
        };

        byte[] lut = DisplayService.BuildPreviewLut(broken);

        Assert.All(lut, v => Assert.InRange(v, (byte)0, (byte)255));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public void The_blue_light_filter_only_moves_when_it_is_switched_on(int level)
    {
        DisplayPreset source = DisplayPreset.Flat();

        DisplayPreset result = DisplayPreset.WithBlueLight(source, level);

        Assert.Equal("flat", result.Id);
        if (level == 0)
        {
            Assert.Equal(source.RedGain, result.RedGain);
            Assert.Equal(source.BlueGain, result.BlueGain);
        }
        else
        {
            Assert.True(result.RedGain >= source.RedGain);
            Assert.True(result.BlueGain <= source.BlueGain);
        }
    }

    [Fact]
    public void The_blue_light_filter_does_not_mutate_the_preset_it_was_given()
    {
        DisplayPreset source = DisplayPreset.Defaults.First(p => p.Id == "camper");
        double before = source.RedGain;

        DisplayPreset.WithBlueLight(source, 2);

        Assert.Equal(before, source.RedGain);
    }

    [Fact]
    public void Every_shipped_display_preset_has_a_distinct_id()
    {
        List<string> ids = DisplayPreset.Defaults.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_shipped_display_preset_keeps_its_channel_gains_in_range()
    {
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            Assert.InRange(preset.RedGain, DisplayPreset.ChannelGainMin, DisplayPreset.ChannelGainMax);
            Assert.InRange(preset.GreenGain, DisplayPreset.ChannelGainMin, DisplayPreset.ChannelGainMax);
            Assert.InRange(preset.BlueGain, DisplayPreset.ChannelGainMin, DisplayPreset.ChannelGainMax);
        }
    }
}
