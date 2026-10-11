using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Undo: one step back from a preset the user deliberately loaded.
/// <para>
/// The rule that matters is which loads are worth remembering. An auto-load is the
/// app deciding on its own, and a revert or a panic key is the app cleaning up -
/// putting either of those behind Undo would turn the button into a way to argue
/// with the watcher rather than a way to take back what you just did. Only a
/// load the user asked for counts.
/// </para>
/// </summary>
public class UndoLoadTests
{
    private static string Slots()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", "MainWindow.Slots.cs"));
    }

    private static string Read(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "app", name));
    }

    [Fact]
    public void OnlyDeliberateLoadsAreRemembered()
    {
        // PlaySlot is reached from three places: a hotkey or the tray or the board
        // (deliberate), a game launching, and a focus pause resuming. Only the first
        // may mark something as undoable, and the way that is guaranteed is a
        // parameter that has to be asked for rather than inferred.
        string slots = Slots();

        Assert.Contains(
            "private void PlaySlot(HotkeySlot slot, bool announce, bool screenOnly = false, bool deliberate = false)",
            slots,
            StringComparison.Ordinal);

        // Exactly one call site opts in. Every other call takes the default, so an
        // auto-load cannot start being undoable by accident.
        int optIns = 0;
        int at = 0;
        while (true)
        {
            int found = slots.IndexOf("deliberate: true", at, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            optIns++;
            at = found + 1;
        }

        Assert.Equal(1, optIns);
    }

    [Fact]
    public void ChoosingAPresetFromEitherBoxIsRemembered()
    {
        // Both boxes, because a user can be moving the screen and the sound
        // independently and an Undo that only covered half of what they just did
        // would be worse than none.
        string display = Read("MainWindow.Display.cs");
        string audio = Read("MainWindow.Audio.cs");

        Assert.Contains("RememberForUndo();", display, StringComparison.Ordinal);
        Assert.Contains("RememberForUndo();", audio, StringComparison.Ordinal);
    }

    [Fact]
    public void RevertingAndPanickingAreNotRemembered()
    {
        // The negative half, which is the half that is easy to lose. A revert
        // happens on its own, with the user somewhere else; if it were undoable,
        // the next deliberate load would be Undo's way back to a preset the app
        // had already taken away.
        //
        // Exactly one call in the whole slot file, and it is inside the deliberate
        // branch of PlaySlot. The auto-load and the focus-pause resume go through
        // the same method and cannot reach it.
        string slots = Slots();

        Assert.Equal(1, CountOccurrences(slots, "RememberForUndo();"));

        // And it really is inside that branch rather than merely near it.
        int playSlot = slots.IndexOf("private void PlaySlot(", StringComparison.Ordinal);
        Assert.True(playSlot >= 0, "PlaySlot has moved");
        Assert.Contains(
            "if (deliberate)",
            slots[playSlot..Math.Min(slots.Length, playSlot + 2000)],
            StringComparison.Ordinal);
    }

    [Fact]
    public void UndoGoesThroughTheSameLoadPathThePresetBoxesUse()
    {
        // A parallel path would leave the profile, the tray tooltip and the live
        // labels describing a load that never happened - which is the exact failure
        // this app already fixed once for the preset boxes.
        string slots = Slots();

        Assert.Contains("LoadTune(", slots, StringComparison.Ordinal);
        Assert.Contains("ApplyDisplay(", slots, StringComparison.Ordinal);
        Assert.Contains("ApplyAudio(", slots, StringComparison.Ordinal);
    }

    [Fact]
    public void UndoIsSpentAfterOneUse()
    {
        // Not a stack, deliberately. A stack means deciding what it means when a
        // preset has been deleted since it was pushed, and a menu to choose from.
        // One step means a line that either has somewhere to go or does not.
        string slots = Slots();

        Assert.Contains("private bool _undoAvailable;", slots, StringComparison.Ordinal);
        Assert.Contains("Nothing to undo", slots, StringComparison.Ordinal);
    }

    [Fact]
    public void UndoIsReachableFromTheTray()
    {
        // The window is usually closed, which is the whole reason this lives in
        // the tray rather than as another button on a board that already scrolls.
        string tray = Read("Services/TrayService.cs");
        string window = Read("MainWindow.xaml.cs");

        Assert.Contains("UndoRequested", tray, StringComparison.Ordinal);
        Assert.Contains("SetUndoAvailable", tray, StringComparison.Ordinal);
        Assert.Contains("_tray.UndoRequested", window, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;

        while (true)
        {
            int found = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            at = found + 1;
        }
    }
}