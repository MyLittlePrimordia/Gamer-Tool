using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The loud guard: a floor on the engine's volume levelling, and nothing else.
/// <para>
/// Confirmed against the engine before it was built. <c>BuildApplyCommand</c>
/// emits <c>--volume_leveling</c> and the value came back out of status.json
/// unchanged, so the guard works by asking the engine to do the thing it is for
/// rather than by moving the Windows output level. That distinction is the whole
/// design: a meter reacting to what it measures can pull the master down on a
/// transient and nothing would ever push it back.
/// </para>
/// <para>
/// The rule that matters most here is that the guard never touches the stored
/// tune. It is applied to a copy on the way to the engine, and the copy is what
/// gets verified against the engine's answer.
/// </para>
/// </summary>
public class LoudGuardTests
{
    private static AudioPreset WithLeveling(double leveling)
    {
        AudioPreset preset = AudioPreset.Flat();
        preset.VolumeLeveling = leveling;
        return preset;
    }

    [Fact]
    public void Off_by_default()
    {
        // A utility that alters audio behind a switch nobody pressed is worse than
        // one that does not offer it.
        Assert.False(new AppSettings().LoudGuard);
        Assert.False(new AudioService().LoudGuardEnabled);
    }

    [Fact]
    public void With_the_guard_off_the_tune_is_sent_exactly_as_it_is()
    {
        AudioPreset original = WithLeveling(0.0);

        AudioPreset sent = AudioService.WithLoudGuard(original, enabled: false);

        Assert.Same(original, sent);
        Assert.Equal(0.0, sent.VolumeLeveling);
    }

    [Fact]
    public void The_guard_lifts_a_tune_that_asks_for_no_leveling()
    {
        AudioPreset sent = AudioService.WithLoudGuard(WithLeveling(0.0), enabled: true);

        Assert.Equal(AudioService.GuardLeveling, sent.VolumeLeveling);
    }

    [Fact]
    public void The_guard_does_not_pull_a_tune_down_that_already_wants_more()
    {
        // A floor, not a replacement. The built-in presets range up to the maximum
        // of four, and the guard must not cap a tune that deliberately wants more.
        AudioPreset sent = AudioService.WithLoudGuard(WithLeveling(4.0), enabled: true);

        Assert.Equal(4.0, sent.VolumeLeveling);
    }

    [Fact]
    public void The_guard_leaves_a_tune_already_at_or_above_it_alone()
    {
        AudioPreset sent = AudioService.WithLoudGuard(WithLeveling(AudioService.GuardLeveling), enabled: true);

        Assert.Equal(AudioService.GuardLeveling, sent.VolumeLeveling);
    }

    [Fact]
    public void The_stored_tune_is_never_modified()
    {
        // The panel is showing this object and the settings will save it. If the
        // guard edited it in place then turning the guard back off would leave the
        // saved tune quietly lifted, with the faders disagreeing with the engine.
        AudioPreset stored = WithLeveling(0.0);

        AudioPreset sent = AudioService.WithLoudGuard(stored, enabled: true);

        Assert.NotSame(stored, sent);
        Assert.Equal(0.0, stored.VolumeLeveling);
        Assert.Equal(AudioService.GuardLeveling, sent.VolumeLeveling);
    }

    [Fact]
    public void Applying_the_guard_twice_changes_nothing()
    {
        // Verify grades against the guarded tune and Apply sends the guarded
        // tune, so the guard runs on both sides. It has to be idempotent.
        AudioPreset once = AudioService.WithLoudGuard(WithLeveling(0.0), enabled: true);
        AudioPreset twice = AudioService.WithLoudGuard(once, enabled: true);

        Assert.Equal(once.VolumeLeveling, twice.VolumeLeveling);
    }

    [Fact]
    public void The_guard_value_is_inside_what_the_engine_accepts()
    {
        Assert.InRange(
            AudioService.GuardLeveling,
            AudioPreset.LevelingMin,
            AudioPreset.LevelingMax);
    }

    [Fact]
    public void The_guard_does_not_disturb_the_rest_of_the_tune()
    {
        AudioPreset stored = WithLeveling(0.0);
        stored.MasterGain = -4.0;
        stored.BassBoost = 3.0;
        stored.Name = "My Tune";

        AudioPreset sent = AudioService.WithLoudGuard(stored, enabled: true);

        Assert.Equal(-4.0, sent.MasterGain);
        Assert.Equal(3.0, sent.BassBoost);
        Assert.Equal("My Tune", sent.Name);
        Assert.Equal(stored.Bands, sent.Bands);
    }

    [Fact]
    public void The_setting_survives_a_round_trip_through_the_schema_guard()
    {
        AppSettings settings = new() { LoudGuard = true };

        Assert.True(ProfileManager.Normalize(settings).LoudGuard);

        // And a profile written before the switch existed comes up with it off
        // rather than throwing or defaulting it on.
        AppSettings older = ProfileManager.Normalize(new AppSettings());
        Assert.False(older.LoudGuard);
    }

    [Fact]
    public void The_command_that_goes_to_the_engine_carries_the_guarded_value()
    {
        AudioService audio = new();

        string command = audio.BuildApplyCommand(
            AudioService.WithLoudGuard(WithLeveling(0.0), enabled: true),
            string.Empty);

        Assert.Contains(
            "--volume_leveling=" + AudioService.GuardLeveling.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            command,
            System.StringComparison.Ordinal);
    }
}
