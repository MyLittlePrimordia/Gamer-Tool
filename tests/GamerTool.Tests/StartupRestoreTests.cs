using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The remembered preset is an identity, not a description of the hardware: the
/// exit path puts the screen and sound back before quitting. Startup has to
/// re-assert the identity, or the dropdown names a preset nothing is using.
/// <para>
/// The rule that matters is which ids are worth re-asserting. Flat and deleted
/// ids are not, and getting that wrong is not cosmetic: a flat restore would
/// overwrite whatever the user has chosen in FxSound or Windows.
/// </para>
/// </summary>
public class StartupRestoreTests
{
    [Theory]
    [InlineData("voice-chat")]
    [InlineData("my-sound")]
    [InlineData("A Preset With Spaces")]
    public void ANamedPresetThatStillExistsIsRestored(string id)
    {
        Assert.True(StartupRestore.ShouldApply(id, presetExists: true));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("FLAT")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FlatOrMissingIdsAreLeftAlone(string? id)
    {
        Assert.False(StartupRestore.ShouldApply(id, presetExists: true));
    }

    [Fact]
    public void APresetThatWasDeletedIsNotRestored()
    {
        Assert.False(StartupRestore.ShouldApply("deleted", presetExists: false));
    }

    /// <summary>
    /// The audio restore has to run after the output repair, or it pushes the
    /// remembered tune at a device the app has just decided is gone. The repair
    /// writes the new output into the profile; ApplyAudio reads the picker, so
    /// the order is the whole correctness of the pairing.
    /// </summary>
    [Fact]
    public void TheAudioRestoreRunsAfterTheStartupOutputRepair()
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.xaml.cs"));
        int repair = source.IndexOf("EnsureUsableAudioOutput(repair, choices);", StringComparison.Ordinal);
        int restore = source.IndexOf("RestoreStartupAudioAsync(startupAudioId)", StringComparison.Ordinal);

        Assert.True(repair >= 0, "could not find the startup output repair");
        Assert.True(
            restore > repair,
            "the startup audio restore must run after EnsureUsableAudioOutput");
    }

    /// <summary>
    /// The display restore has to run after the schedule's first evaluation, so
    /// a running night filter is folded in by ApplyDisplay rather than replaced
    /// by a preset that was saved before the filter came on.
    /// </summary>
    [Fact]
    public void TheDisplayRestoreRunsAfterTheNightSchedulesFirstEvaluation()
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.xaml.cs"));
        int night = source.IndexOf("StartNightScheduleTimer();", StringComparison.Ordinal);
        int restore = source.IndexOf("RestoreStartupDisplay();", StringComparison.Ordinal);

        Assert.True(night >= 0, "could not find the night schedule start");
        Assert.True(
            restore > night,
            "the startup display restore must run after StartNightScheduleTimer");
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "app");
    }
}
