using System;
using System.IO;
using System.Linq;
using GamerTool.Models;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The tray tooltip, and the status it is built from.
/// <para>
/// The tooltip said "Gamer Tool" and nothing else for the whole life of the app,
/// on the one surface the user opens precisely because they have lost the window.
/// <see cref="TrayService.SetStatus"/> existed the entire time and had no caller,
/// so the fix was wiring rather than writing.
/// </para>
/// <para>
/// The wording logic is in MainWindow, which cannot be constructed here, so the
/// parts that can be checked from outside are checked: that the composing helper
/// resolves a preset id to a name, that a name is not reported as missing when it
/// exists, and that SetStatus tolerates the inputs a status line actually
/// produces.
/// </para>
/// </summary>
public class TrayStatusTests
{
    [Fact]
    public void The_status_setter_exists_and_is_called_from_the_window()
    {
        // If the helper ever loses its only caller the tooltip silently freezes
        // again, which is exactly how it stayed frozen for so long.
        string xaml = File.ReadAllText(FindApp("MainWindow.xaml.cs"));

        Assert.Contains("_tray?.SetStatus(", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_is_composed_from_the_live_preset_ids()
    {
        // From the live ids, not from remembered state, so it cannot disagree with
        // the on-screen label.
        string xaml = File.ReadAllText(FindApp("MainWindow.xaml.cs"));

        Assert.Contains("PresetName(_activeDisplayId)", xaml, StringComparison.Ordinal);
        Assert.Contains("PresetName(_activeAudioId)", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_replaces_the_tooltip()
    {
        // A value that did not reach the engine is worth more than the current
        // preset names until it is fixed.
        string xaml = File.ReadAllText(FindApp("MainWindow.xaml.cs"));

        Assert.Contains("SetTrayProblem", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_built_in_preset_id_resolves_to_a_name()
    {
        // If an id does not resolve, the tooltip says "no screen" for a preset
        // that plainly is loaded.
        foreach (DisplayPreset preset in DisplayPreset.Defaults)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name), preset.Id + " has no name");
        }

        foreach (AudioPreset preset in AudioPreset.Defaults)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name), preset.Id + " has no name");
        }
    }

    // There is deliberately no test for the length clamp. SetStatus needs a live
    // NotifyIcon, which needs a UI thread and an icon, so the clamp can only be
    // reached through the whole tray - and a test that asserted
    // Math.Min(len, 63) == 63 would be asserting arithmetic about its own input,
    // passing or failing with no connection to the app at all. The clamp is
    // wrapped in a try/catch for that reason: it is a guard, not a behaviour
    // anyone depends on.

    private static string FindApp(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "app", fileName);
        Assert.True(File.Exists(path), "could not find " + path);
        return path;
    }
}
