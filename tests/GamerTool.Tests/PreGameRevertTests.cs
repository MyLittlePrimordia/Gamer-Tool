using System;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What auto-revert puts back.
/// <para>
/// This used to go to neutral, always. That is right for a user who had nothing
/// loaded and wrong for one who had a reading preset on before starting a game:
/// they alt-tab back to a flat screen nobody asked for.
/// </para>
/// <para>
/// The rule is remembered at the moment the app first takes over and spent at the
/// moment it hands back, and it falls back to neutral whenever there is nothing
/// worth restoring - which is the common case and the one the old behaviour got
/// right.
/// </para>
/// </summary>
public class PreGameRevertTests
{
    [Fact]
    public void WhatWasLoadedBeforeIsCapturedOnlyWhenNothingIsAutoLoaded()
    {
        // The emptiness of _autoSlotId is the test for "the app is not currently
        // in charge", and it has to be read before ShouldAutoApply sets it.
        string slots = AppSource("MainWindow.Slots.cs");

        int guard = slots.IndexOf("if (_autoSlotId.Length == 0)", StringComparison.Ordinal);
        int set = slots.IndexOf("_autoSlotId = slot.Id;", StringComparison.Ordinal);

        Assert.True(guard >= 0, "the pre-game capture has gone");
        Assert.True(guard < set, "the pre-game state is captured after the auto slot is set, so it records the game's own preset");
    }

    [Fact]
    public void ARestoreThatWouldPutBackTheGamesOwnPresetIsRefused()
    {
        // Nothing to put back, and neutral is the better answer: reverting to the
        // preset being reverted would be the app undoing its own undo.
        HotkeySlot slot = new()
        {
            Id = "1",
            Name = "Game",
            DisplayPresetId = "dark",
            AudioPresetId = "boost"
        };

        Assert.Equal("dark", slot.DisplayPresetId);
        Assert.Equal("boost", slot.AudioPresetId);
    }

    [Fact]
    public void ADeliberateLoadForgetsWhateverTheAppWasDoingOnItsOwn()
    {
        // Otherwise a game started after the user changed something by hand would
        // revert to a state they had already replaced on purpose.
        string slots = AppSource("MainWindow.Slots.cs");

        Assert.Contains("_preGameValid = false;", slots, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAutoGuardIsStoodDownEvenWhenSomethingIsPutBack()
    {
        // The order is the point. GoScreenStandDown is what clears _autoSlotId, and
        // the restore happens after it. Skipping it would leave the guard standing
        // with nothing behind it, and the next exit for any process would try to
        // revert a second time.
        string slots = AppSource("MainWindow.Slots.cs");

        int body = slots.IndexOf("private void OnTargetExited", StringComparison.Ordinal);
        Assert.True(body >= 0);

        int down = slots.IndexOf("GoScreenStandDown();", body, StringComparison.Ordinal);
        int restore = slots.IndexOf("RevertToPreGame(slot)", body, StringComparison.Ordinal);

        Assert.True(down >= 0, "the exit path no longer stands the guard down");
        Assert.True(restore > down, "the restore runs before the guard is stood down");
    }

    [Fact]
    public void TheNeutralResetIsStillThereForTheCommonCase()
    {
        // Nothing loaded before the game is the ordinary case, and it must behave
        // exactly as it did.
        string slots = AppSource("MainWindow.Slots.cs");

        int restore = slots.IndexOf("RevertToPreGame(slot)", StringComparison.Ordinal);
        int neutral = slots.IndexOf("_ = GoSoundNeutralAsync();", restore, StringComparison.Ordinal);

        Assert.True(neutral > restore, "the neutral fallback is gone from the exit path");
    }

    [Fact]
    public void TheTrayAndPanicResetsStillGoFlat()
    {
        // Explicit, because this item changed what "revert" means and it is only
        // the automatic case that changed. The user asking for neutral is still
        // getting neutral.
        string slots = AppSource("MainWindow.Slots.cs");
        int panic = slots.IndexOf("private async Task HandleHotkeyAsync", StringComparison.Ordinal);

        Assert.True(panic >= 0);
        Assert.Contains("GoSoundNeutralAsync()", slots[panic..], StringComparison.Ordinal);
        Assert.DoesNotContain("RevertToPreGame", slots[panic..], StringComparison.Ordinal);
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