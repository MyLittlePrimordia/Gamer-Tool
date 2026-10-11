using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Who gets their FxSound back on the way out.
/// <para>
/// A great many people run FxSound as their player, with their own preset, and
/// installed this app alongside it. The exit path used to push a flat curve and
/// then run <c>--power=0</c> on every one of them, which is an app taking over
/// something it was only borrowing.
/// </para>
/// <para>
/// So a snapshot is taken before the first thing this app does to the engine, and
/// handed back on exit. What these test is the judgement about when: a snapshot
/// that cannot be trusted has to fall through to the old behaviour, and the old
/// behaviour is still correct for a machine that only has FxSound because this app
/// put it there.
/// </para>
/// </summary>
public class FxRestoreOnExitTests
{
    private const string UsersOwnPreset = "Late Night Jazz";

    [Fact]
    public void AUserListeningToSomethingOfTheirOwnGetsItBack()
    {
        Assert.True(EmergencyReset.ShouldRestoreUserEngine(
            new FxSnapshot(Power: true, Preset: UsersOwnPreset, Output: "Speakers")));
    }

    [Fact]
    public void NoSnapshotMeansTheOldBehaviour()
    {
        // The engine was not running, does not report its power state, or only had
        // a stale status file. None of those is knowledge, and guessing is worse
        // than the flat reset this replaces.
        Assert.False(EmergencyReset.ShouldRestoreUserEngine(null));
    }

    [Fact]
    public void AnEngineTheUserHadSwitchedOffStaysOff()
    {
        // They chose that. Restoring into it would be the app deciding otherwise on
        // the way out.
        Assert.False(EmergencyReset.ShouldRestoreUserEngine(
            new FxSnapshot(Power: false, Preset: UsersOwnPreset, Output: string.Empty)));
    }

    [Fact]
    public void AnEngineWithNoPresetSelectedHasNothingToHandBack()
    {
        Assert.False(EmergencyReset.ShouldRestoreUserEngine(
            new FxSnapshot(Power: true, Preset: string.Empty, Output: "Speakers")));
    }

    [Fact]
    public void ALeftoverGamerToolPresetIsNotTheUsersChoice()
    {
        // The important negative. If the app crashed while a game preset was
        // loaded, the engine's selected preset is GamerTool's own file - and
        // restoring that would mean quitting with a game's equaliser still on the
        // screen, which is the single outcome this exit path exists to prevent.
        Assert.False(EmergencyReset.ShouldRestoreUserEngine(
            new FxSnapshot(Power: true, Preset: FxPresetFile.PresetName, Output: string.Empty)));

        // Case-insensitively: the name comes out of a file another program wrote.
        Assert.False(EmergencyReset.ShouldRestoreUserEngine(
            new FxSnapshot(Power: true, Preset: FxPresetFile.PresetName.ToUpperInvariant(), Output: string.Empty)));
    }

    [Fact]
    public void TheOutputDeviceIsNotWhatDecidesIt()
    {
        // Only one thing about the snapshot is allowed to be required - a preset
        // the user chose. Requiring an output device as well would mean a user on
        // the engine's default device, which is most of them, quietly stopped
        // getting their sound back.
        Assert.True(EmergencyReset.ShouldRestoreUserEngine(
            new FxSnapshot(Power: true, Preset: UsersOwnPreset, Output: string.Empty)));
    }

    [Fact]
    public void TheSnapshotIsTakenOnceAndNotOnEveryPush()
    {
        // Twice would record this app's own work as the user's preference, which
        // is the failure the whole feature is built to avoid. Asserted by seeding
        // the state rather than by reading a live engine, so the answer is the
        // same on a machine with FxSound and one without.
        SessionState state = new()
        {
            FxBefore = new FxSnapshot(Power: true, Preset: UsersOwnPreset, Output: string.Empty)
        };

        // Whatever is on the machine, the first capture is not allowed to replace
        // what is already there.
        state.CaptureFxBefore(new AudioService());

        Assert.Equal(UsersOwnPreset, state.FxBefore!.Preset);
    }

    [Fact]
    public void NoServiceMeansNoSnapshot()
    {
        // A window with no engine cannot have had one, so there is nothing to
        // record and the exit path keeps its original behaviour.
        SessionState state = new();

        state.CaptureFxBefore(null!);

        Assert.Null(state.FxBefore);
    }

    [Fact]
    public void TheTrayResetStillFlattens()
    {
        // Explicit, because it is the one that could have been swept up in this.
        // "Reset sound" from the tray is the user asking for neutral; only the
        // on-exit path changes.
        string window = AppSource("MainWindow.Audio.cs");

        Assert.Contains("GoSoundNeutralAsync", window, System.StringComparison.Ordinal);
        Assert.Contains("AudioPreset.Flat()", window, System.StringComparison.Ordinal);

        // And FlattenAndPowerDown, which is what the exit falls back to, is not
        // reachable from the window at all.
        Assert.DoesNotContain("FlattenAndPowerDown", window, System.StringComparison.Ordinal);
    }

    private static string AppSource(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", name));
    }
}