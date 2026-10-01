using System;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Reading the audio engine's snapshot.
/// <para>
/// The whole file was read in one try, and three of the field readers called
/// <c>GetString()</c> without checking what they had while a fourth - written
/// later, for the preset lists - did check. So one field that arrived as a number
/// rather than a string threw, and the entire snapshot went with it: every band
/// gain, every effect, the output list. On a four second poll that is a log line
/// every four seconds rotating out the lines that were worth reading.
/// </para>
/// <para>
/// These are all somebody else's numbers. The engine writes this file and its
/// shape is not this app's to assume, so every field is asked for defensively
/// and a value that cannot be understood becomes a fallback rather than an
/// exception.
/// </para>
/// </summary>
public class FxSoundStateParseTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A snapshot with everything a healthy engine sends.</summary>
    private const string Full = """
        {
          "version": "1.2.15.0",
          "power": true,
          "presets": {
            "built_in": [ { "name": "Music", "modified": false }, "Volume Boost" ],
            "user_defined": [ { "name": "GamerTool", "modified": true } ]
          },
          "selected_preset": "GamerTool",
          "selected_output": "Speakers",
          "output_devices": [ "Speakers", "Headphones" ],
          "equalizer": {
            "num_bands": 10,
            "master_gain": -2.5,
            "volume_leveling": 3.0,
            "filter_q": 1.2,
            "balance": 0.0,
            "bands": [
              { "index": 0, "frequency": 31.0, "gain": 1.5 },
              { "index": 1, "frequency": 62.0, "gain": -2.0 }
            ]
          },
          "effects": { "clarity": 1.0, "ambience": 2.0, "surround": 3.0, "dynamicboost": 4.0, "bass": 5.0 }
        }
        """;

    [Fact]
    public void A_healthy_snapshot_is_read_completely()
    {
        FxSoundState? state = FxSoundState.Parse(Full, Stamp);

        Assert.NotNull(state);
        Assert.Equal("1.2.15.0", state!.Version);
        Assert.True(state.Power);
        Assert.Equal("GamerTool", state.SelectedPreset);
        Assert.Equal("Speakers", state.SelectedOutput);
        Assert.Equal(new[] { "Speakers", "Headphones" }, state.OutputDevices);
        Assert.Equal(new[] { "Music", "Volume Boost" }, state.BuiltInPresets);
        Assert.Equal(new[] { "GamerTool" }, state.UserPresets);
        Assert.Equal(10, state.Equalizer.NumBands);
        Assert.Equal(-2.5, state.Equalizer.MasterGain);
        Assert.Equal(3.0, state.Equalizer.VolumeLeveling);
        Assert.Equal(2, state.Equalizer.Bands.Count);
        Assert.Equal(1.5, state.Equalizer.Bands[0].Gain);
        Assert.Equal(5.0, state.Effects.Bass);
    }

    [Fact]
    public void A_version_that_is_a_number_does_not_discard_the_snapshot()
    {
        // GetString() throws on this. Used to lose everything.
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace("\"1.2.15.0\"", "1.2", StringComparison.Ordinal), Stamp);

        Assert.NotNull(state);
        Assert.Equal(string.Empty, state!.Version);

        // The part that matters: the bands are still there.
        Assert.Equal(2, state.Equalizer.Bands.Count);
        Assert.Equal(5.0, state.Effects.Bass);
    }

    [Fact]
    public void A_band_count_written_as_a_float_does_not_discard_the_snapshot()
    {
        // JSON numbers are not typed the way command line arguments are, so
        // GetInt32() throws on 10.0.
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace("\"num_bands\": 10", "\"num_bands\": 10.0", StringComparison.Ordinal), Stamp);

        Assert.NotNull(state);
        Assert.Equal(10, state!.Equalizer.NumBands);
        Assert.Equal(2, state.Equalizer.Bands.Count);
    }

    [Fact]
    public void A_band_count_beyond_the_range_of_an_int_falls_back_rather_than_throwing()
    {
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace("\"num_bands\": 10,", "\"num_bands\": 99999999999999,", StringComparison.Ordinal), Stamp);

        Assert.NotNull(state);

        // Not the exploded value, and not a thrown exception: the ten band
        // default, which is what the app assumes anyway.
        Assert.Equal(10, state!.Equalizer.NumBands);
        Assert.Equal(2, state.Equalizer.Bands.Count);
    }

    [Fact]
    public void An_output_device_that_is_not_a_string_is_skipped_not_fatal()
    {
        // An object where a name was expected. GetString() throws on this, and
        // used to lose the whole snapshot with it.
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace(
                "\"output_devices\": [ \"Speakers\", \"Headphones\" ]",
                "\"output_devices\": [ { \"name\": \"Speakers\" }, \"Headphones\" ]",
                StringComparison.Ordinal),
            Stamp);

        Assert.NotNull(state);

        // The one that can be understood survives; the shape we cannot is skipped.
        Assert.Equal(new[] { "Headphones" }, state!.OutputDevices);
        Assert.Equal(2, state.Equalizer.Bands.Count);
    }

    [Fact]
    public void A_preset_entry_that_is_an_object_rather_than_a_name_is_understood()
    {
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace("[ { \"name\": \"Music\", \"modified\": false }, \"Volume Boost\" ]",
                "[ { \"name\": \"Music\", \"modified\": false }, { \"title\": \"Volume Boost\" } ]",
                StringComparison.Ordinal),
            Stamp);

        Assert.NotNull(state);
        Assert.Equal(new[] { "Music" }, state!.BuiltInPresets);
    }

    [Fact]
    public void Selected_fields_of_the_wrong_type_are_treated_as_absent()
    {
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace("\"selected_preset\": \"GamerTool\"", "\"selected_preset\": 7", StringComparison.Ordinal)
                .Replace("\"selected_output\": \"Speakers\"", "\"selected_output\": false", StringComparison.Ordinal),
            Stamp);

        Assert.NotNull(state);
        Assert.Equal(string.Empty, state!.SelectedPreset);
        Assert.Equal(string.Empty, state.SelectedOutput);
        Assert.Equal(2, state.Equalizer.Bands.Count);
    }

    [Fact]
    public void An_effect_that_is_a_string_falls_back_to_nothing_rather_than_throwing()
    {
        FxSoundState? state = FxSoundState.Parse(
            Full.Replace("\"bass\": 5.0", "\"bass\": \"lots\"", StringComparison.Ordinal), Stamp);

        Assert.NotNull(state);
        Assert.Equal(0.0, state!.Effects.Bass);
        Assert.Equal(1.0, state.Effects.Clarity);
    }

    [Fact]
    public void Text_that_is_not_json_is_nothing_rather_than_an_exception()
    {
        Assert.Null(FxSoundState.Parse("the engine is restarting", Stamp));
        Assert.Null(FxSoundState.Parse(string.Empty, Stamp));
        Assert.Null(FxSoundState.Parse("   ", Stamp));
    }

    [Fact]
    public void An_empty_object_still_reads_as_a_state_with_defaults()
    {
        FxSoundState? state = FxSoundState.Parse("{}", Stamp);

        Assert.NotNull(state);
        Assert.Equal(string.Empty, state!.Version);
        Assert.False(state.Power);
        Assert.Equal(10, state.Equalizer.NumBands);
        Assert.Empty(state.Equalizer.Bands);
    }

    [Fact]
    public void The_file_timestamp_is_carried_through_so_freshness_can_be_judged()
    {
        // Just written, so it counts as live.
        FxSoundState? fresh = FxSoundState.Parse(Full, DateTime.UtcNow);
        Assert.Equal(DateTime.UtcNow, fresh!.FileWrittenUtc, TimeSpan.FromSeconds(5));
        Assert.True(fresh.IsFresh);

        // Old enough that it must not be presented as the engine's current state.
        // ReadState zeroes this stamp deliberately when it falls back to a cached
        // file, and the panel is expected to treat that as not live.
        FxSoundState? stale = FxSoundState.Parse(Full, DateTime.UtcNow.AddMinutes(-5));
        Assert.False(stale!.IsFresh);
    }

    [Fact]
    public void No_band_is_lost_when_a_middle_one_is_unreadable()
    {
        // The failure this guards against lost all ten bands to one bad index.
        FxSoundState? state = FxSoundState.Parse(
            """
            { "equalizer": { "num_bands": 3, "bands": [
                { "index": 0, "frequency": 31.0, "gain": 1.0 },
                { "index": "one", "frequency": 62.0, "gain": 2.0 },
                { "index": 2, "frequency": 125.0, "gain": 3.0 } ] } }
            """,
            Stamp);

        Assert.NotNull(state);
        Assert.Equal(3, state!.Equalizer.Bands.Count);

        // The odd one falls back to its own position in the list, which is the
        // only sensible thing available, and the other two keep their numbers
        // rather than shifting.
        Assert.Equal(3.0, state.Equalizer.Bands[2].Gain);
        Assert.Equal(2, state.Equalizer.Bands[2].Index);
        Assert.Equal(1, state.Equalizer.Bands[1].Index);
    }
}
