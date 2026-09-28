using System.Text.Json;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The bypass switch, the setting behind it, and the rename that came between
/// them.
/// <para>
/// The switch is labelled BYPASS and the setting says whether the chain is live,
/// so the two read opposite ways round and one conversion sits between them. That
/// conversion was missing: the switch was wired straight to a setting called
/// <c>BypassEnabled</c> whose <c>true</c> meant the equaliser was running, so the
/// app opened with BYPASS showing on while the curve was live, and throwing the
/// switch silenced the chain instead of engaging a bypass.
/// </para>
/// <para>
/// Both halves are asserted against each other here rather than on their own,
/// because either one alone passes just as happily when the pair is inverted. A
/// test that only checks the setting round-trips, or only that the switch maps to
/// a bool, is exactly what let this through.
/// </para>
/// </summary>
public class BypassToggleTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_switch_is_engaged_exactly_when_the_effects_are_off(bool effectsEnabled, bool engaged)
    {
        Assert.Equal(engaged, BypassToggle.IsEngaged(effectsEnabled));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_setting_follows_the_switch(bool engaged, bool effectsEnabled)
    {
        Assert.Equal(effectsEnabled, BypassToggle.EffectsFromSwitch(engaged));
    }

    [Fact]
    public void The_setting_and_the_switch_survive_a_trip_through_each_other()
    {
        // The property that actually matters, and the one a sign error breaks.
        foreach (bool effectsEnabled in new[] { true, false })
        {
            bool back = BypassToggle.EffectsFromSwitch(BypassToggle.IsEngaged(effectsEnabled));
            Assert.Equal(effectsEnabled, back);
        }
    }

    [Fact]
    public void What_the_switch_shows_agrees_with_what_the_engine_would_be_sent()
    {
        // The switch's visible state and the audio it stands for, checked together
        // because a mismatch is the whole bug: the old wiring showed the switch
        // engaged while the service was sending the preset's own gains.
        foreach (bool effectsEnabled in new[] { true, false })
        {
            var audio = new AudioService { EffectsEnabled = effectsEnabled };
            var preset = Tuned();

            bool engaged = BypassToggle.IsEngaged(effectsEnabled);
            double firstBand = audio.ExpectedBandGain(preset, 0);
            double bass = audio.ExpectedEffectValues(preset)[4];

            if (engaged)
            {
                // Showing BYPASS: the chain has to be flat and silent.
                Assert.Equal(0.0, firstBand);
                Assert.Equal(0.0, bass);
            }
            else
            {
                // Not showing BYPASS: the curve and the effects have to be live.
                Assert.Equal(6.0, firstBand);
                Assert.Equal(6.0, bass);
            }
        }
    }

    [Fact]
    public void The_bypass_command_flattens_the_curve_and_the_effects()
    {
        var preset = Tuned();

        string engaged = new AudioService { EffectsEnabled = false }.BuildBypassCommand(preset);
        Assert.Contains("--set_band_gain=\"0:0.00,1:0.00,2:0.00,3:0.00,4:0.00,5:0.00,6:0.00,7:0.00,8:0.00,9:0.00\"", engaged);
        Assert.Contains("--set_effect=\"clarity:0.0,ambience:0.0,surround:0.0,dynamicboost:0.0,bass:0.0\"", engaged);

        string released = new AudioService { EffectsEnabled = true }.BuildBypassCommand(preset);
        Assert.Contains("--set_band_gain=\"0:6.00,1:4.00,2:2.00,3:0.00,4:-2.00,5:0.00,6:1.00,7:3.00,8:5.00,9:6.00\"", released);
        Assert.Contains("--set_effect=\"clarity:5.0,ambience:0.0,surround:0.0,dynamicboost:0.0,bass:6.0\"", released);
    }

    /// <summary>
    /// A tune with something in every position the bypass is supposed to flatten,
    /// so a test cannot pass by comparing zeros against zeros.
    /// </summary>
    private static AudioPreset Tuned() => new()
    {
        Name = "t",
        NumBands = 10,
        Bands = new[] { 6.0, 4.0, 2.0, 0.0, -2.0, 0.0, 1.0, 3.0, 5.0, 6.0 },
        Clarity = 5.0,
        BassBoost = 6.0,
    };
}

/// <summary>
/// The BypassEnabled to EffectsEnabled rename, for profiles already on disk.
/// <para>
/// The name changed and the meaning did not, so the value has to carry across
/// untouched. A profile with no key at all has to land on processing enabled, so
/// an install that predates the switch is not left muted, and the legacy key has
/// to stop being written back out once it has been read.
/// </para>
/// </summary>
public class EffectsEnabledRenameTests
{
    private const string LegacyKey = "BypassEnabled";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_profile_saved_under_the_old_name_keeps_its_state(bool wasBypassed)
    {
        // wasBypassed is the old key's value, which is the same thing the new name
        // calls EffectsEnabled: true meant the chain was live.
        string json = "{\"Schema\":1,\"" + LegacyKey + "\":" + (wasBypassed ? "true" : "false") + "}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        ProfileManager.Normalize(loaded);
        loaded.Migrate();

        Assert.Equal(wasBypassed, loaded.EffectsEnabled);
    }

    [Fact]
    public void A_profile_with_no_key_at_all_comes_up_with_the_effects_enabled()
    {
        // The case the schema warning at AppSettings is about: a property
        // initialiser says nothing about a value that was never written, so this
        // has to be the default rather than whatever the initialiser happens to be
        // if it is ever changed.
        string json = "{\"Schema\":1}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        ProfileManager.Normalize(loaded);
        loaded.Migrate();

        Assert.True(loaded.EffectsEnabled);
    }

    [Fact]
    public void An_empty_profile_comes_up_with_the_effects_enabled()
    {
        Assert.True(new AppSettings().EffectsEnabled);
    }

    [Fact]
    public void A_profile_from_before_the_bypass_existed_comes_up_unmuted()
    {
        // A build that had no such setting at all. Nothing to carry across, so the
        // default stands and the user keeps their audio.
        string json = "{\"Schema\":0,\"AntiClip\":true}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        ProfileManager.Normalize(loaded);
        loaded.Migrate();

        Assert.True(loaded.EffectsEnabled);
    }

    [Fact]
    public void The_legacy_key_is_not_written_back_out()
    {
        // Otherwise every save keeps a dead key alive forever and the next reader
        // has to work out which of the two names is the real one.
        string json = "{\"Schema\":1,\"" + LegacyKey + "\":false}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        ProfileManager.Normalize(loaded);
        loaded.Migrate();

        Assert.Null(loaded.LegacyBypassEnabled);

        // Serialised the way ProfileManager does it, so the null is omitted.
        string written = JsonSerializer.Serialize(loaded, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        Assert.DoesNotContain(LegacyKey, written);
        Assert.Contains("\"EffectsEnabled\": false", written.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_restored_profile_keeps_the_state_without_going_through_startup()
    {
        // Restoring a backup replaces the whole profile and never calls Migrate, so
        // Normalize has to be the one that folds the old key in. This is the path
        // that would otherwise quietly drop the state.
        string json = "{\"Schema\":1,\"" + LegacyKey + "\":false}";

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        AppSettings normalized = ProfileManager.Normalize(restored);

        Assert.False(normalized.EffectsEnabled);
    }

    [Fact]
    public void Migrating_twice_does_not_change_the_answer()
    {
        string json = "{\"Schema\":0,\"" + LegacyKey + "\":false}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        ProfileManager.Normalize(loaded);
        loaded.Migrate();
        bool first = loaded.EffectsEnabled;
        loaded.Migrate();
        ProfileManager.Normalize(loaded);

        Assert.Equal(first, loaded.EffectsEnabled);
        Assert.False(loaded.EffectsEnabled);
        Assert.Equal(AppSettings.CurrentSchema, loaded.Schema);
    }

    [Fact]
    public void A_profile_already_on_the_current_schema_is_left_alone()
    {
        string json = "{\"Schema\":" + AppSettings.CurrentSchema + ",\"EffectsEnabled\":true}";

        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        loaded.Migrate();

        Assert.True(loaded.EffectsEnabled);
        Assert.Null(loaded.LegacyBypassEnabled);
    }
}
