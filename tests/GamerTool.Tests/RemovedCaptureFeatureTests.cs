using System.IO;
using System.Linq;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The button that saved what FxSound was doing right now, as a preset of the
/// user's own.
/// <para>
/// Removed as bloat, on the grounds that it was the only control in the app that
/// needed the user to know there were two different things called a preset - the
/// app's, and FxSound's - and to work out which one a button sitting beside "Save
/// Preset" referred to. Every other button in that row acts on the app's own
/// tunes.
/// </para>
/// <para>
/// It was the only path by which FxSound state came inward; everything else pushes
/// a preset out through the command line. That is a real capability and it was
/// there because a curve somebody spent twenty minutes finding inside FxSound
/// could not otherwise be brought back without re-entering ten bands by mouse.
/// </para>
/// <para>
/// So the removal is pinned rather than left to drift. The code and the button
/// coming back is the failure this guards, not the other way round.
/// </para>
/// </summary>
public class RemovedCaptureFeatureTests
{
    private static string AppFile(string relative) =>
        File.ReadAllText(FindRepoFile(Path.Combine("app", relative)));

    [Fact]
    public void The_capture_button_is_gone_from_the_audio_tab()
    {
        string xaml = AppFile("MainWindow.xaml");

        Assert.DoesNotContain("AudioCaptureButton", xaml);
        Assert.DoesNotContain("OnCaptureFromFxSoundClick", xaml);
        Assert.DoesNotContain("Save what FxSound is doing now", xaml);
    }

    [Fact]
    public void The_capture_handler_is_gone()
    {
        Assert.DoesNotContain("OnCaptureFromFxSoundClick", AppFile("MainWindow.Audio.cs"));
        Assert.DoesNotContain("AudioPresetCapture", AppFile("MainWindow.Audio.cs"));
    }

    [Fact]
    public void The_capture_service_is_deleted_rather_than_left_unused()
    {
        // An orphaned pure helper is worse than a removed feature: it keeps its
        // tests, its documentation and its claim to be a supported path, and
        // nothing says any of that is no longer true.
        Assert.False(File.Exists(FindRepoFile(Path.Combine("app", "Services", "AudioPresetCapture.cs"))));

        Assert.False(File.Exists(FindRepoFile(Path.Combine(
            "tests",
            "GamerTool.Tests",
            "AudioPresetCaptureTests.cs"))));
    }

    [Fact]
    public void The_presets_row_still_has_its_three_buttons()
    {
        // The button was first in a row of four. Removing it must not have taken a
        // neighbour with it, since the other three are the app's own presets and
        // are the ones that are staying.
        string xaml = AppFile("MainWindow.xaml");

        Assert.Contains("x:Name=\"AudioSaveButton\"", xaml);
        Assert.Contains("x:Name=\"AudioRenameButton\"", xaml);
        Assert.Contains("x:Name=\"AudioDeleteButton\"", xaml);
        Assert.Contains("OnSaveAsSoundClick", xaml);
        Assert.Contains("OnRenameSoundClick", xaml);
        Assert.Contains("OnDeleteSoundClick", xaml);
    }

    [Fact]
    public void Nothing_still_reports_a_capture_button_or_its_state()
    {
        // The enable logic read the engine's status on every repaint of the Audio
        // tab purely to decide whether that button could be pressed. With the
        // button gone that read has nothing to decide, and it was the only thing
        // forcing an engine status check on a repaint.
        string xamlCode = AppFile("MainWindow.xaml.cs");

        Assert.DoesNotContain("AudioCaptureButton", xamlCode);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}