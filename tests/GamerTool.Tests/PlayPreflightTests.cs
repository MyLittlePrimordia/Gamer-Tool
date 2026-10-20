using System;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What the Play button is allowed to start, and what it says before it does.
/// <para>
/// The target rules are the safety half: a folder or a wildcard describes a set
/// of programs, and picking one of them to start would be the app guessing. The
/// warnings are the useful half: a missing sound preset should not stop a game
/// from starting, but the user should be told before the first gunshot that the
/// profile is not complete.
/// </para>
/// </summary>
public class PlayPreflightTests
{
    private const string Exe = @"C:\Games\Arena\arena.exe";

    private static HotkeySlot Slot() => new()
    {
        Id = "slot_arena",
        Name = "Arena",
        AppExePath = Exe,
        AppName = "Arena",
        DisplayPresetId = "screen_arena",
        AudioPresetId = "sound_arena",
        CrosshairPresetId = "xh_arena",
        MonitorKey = "MONITOR\\ABC123",
    };

    [Fact]
    public void AnExactExeIsTheOnlyThingThatCanBeLaunched()
    {
        Assert.True(PlayPreflight.TryTarget(Slot(), out string path));
        Assert.Equal(Exe, path);

        HotkeySlot folder = Slot();
        folder.TargetFolder = @"C:\Games\Arena";
        folder.AppExePath = null;
        Assert.False(PlayPreflight.TryTarget(folder, out _));

        HotkeySlot wildcard = Slot();
        wildcard.IsAnyGameTarget = true;
        Assert.False(PlayPreflight.TryTarget(wildcard, out _));

        HotkeySlot self = Slot();
        self.IsSelfTarget = true;
        Assert.False(PlayPreflight.TryTarget(self, out _));

        HotkeySlot processOnly = Slot();
        processOnly.AppExePath = null;
        processOnly.AppName = "arena";
        Assert.False(PlayPreflight.TryTarget(processOnly, out _));

        HotkeySlot notExe = Slot();
        notExe.AppExePath = @"C:\Games\Arena\launch.bat";
        Assert.False(PlayPreflight.TryTarget(notExe, out _));

        HotkeySlot relative = Slot();
        relative.AppExePath = @"Games\Arena\arena.exe";
        Assert.False(PlayPreflight.TryTarget(relative, out _));
    }

    [Fact]
    public void ACompleteSlotIsReadyWithNoWarnings()
    {
        PlayPreflight check = PlayPreflight.Build(
            Slot(),
            targetExists: true,
            displayExists: true,
            audioExists: true,
            crosshairExists: true,
            audioInstalled: true,
            monitorResolved: true);

        Assert.True(check.CanLaunch);
        Assert.Empty(check.Warnings);
        Assert.Equal("screen + sound + crosshair", check.Summary);
        Assert.Contains("[x] GAME", check.Tooltip);
        Assert.Contains("start the game", check.Tooltip);
    }

    [Fact]
    public void AMissingGameFileBlocksTheLaunchAndNamesThePath()
    {
        PlayPreflight check = PlayPreflight.Build(
            Slot(),
            targetExists: false,
            displayExists: true,
            audioExists: true,
            crosshairExists: true,
            audioInstalled: true,
            monitorResolved: true);

        Assert.False(check.CanLaunch);
        Assert.Contains(Exe, check.BlockedReason);
        Assert.Contains("[!] GAME", check.Tooltip);
    }

    [Fact]
    public void ASlotWithNothingToApplyIsNotAPlayRequest()
    {
        HotkeySlot slot = Slot();
        slot.DisplayPresetId = null;
        slot.AudioPresetId = null;
        slot.CrosshairPresetId = null;

        PlayPreflight check = PlayPreflight.Build(
            slot,
            targetExists: true,
            displayExists: false,
            audioExists: false,
            crosshairExists: false,
            audioInstalled: true,
            monitorResolved: true);

        Assert.False(check.CanLaunch);
        Assert.Contains("Nothing set", check.BlockedReason);
    }

    [Fact]
    public void AMissingPresetWarnsWithoutRefusingTheGame()
    {
        PlayPreflight check = PlayPreflight.Build(
            Slot(),
            targetExists: true,
            displayExists: false,
            audioExists: true,
            crosshairExists: false,
            audioInstalled: true,
            monitorResolved: true);

        Assert.True(check.CanLaunch);
        Assert.Contains(check.Warnings, w => w.Label == "SCREEN" && w.Detail == "preset missing");
        Assert.Contains(check.Warnings, w => w.Label == "CROSSHAIR" && w.Detail == "preset missing");
        Assert.Equal("sound", check.Summary);
    }

    [Fact]
    public void AMissingFxSoundWarnsWithoutRefusingTheGame()
    {
        PlayPreflight check = PlayPreflight.Build(
            Slot(),
            targetExists: true,
            displayExists: true,
            audioExists: true,
            crosshairExists: true,
            audioInstalled: false,
            monitorResolved: true);

        Assert.True(check.CanLaunch);
        Assert.Contains(check.Warnings, w => w.Label == "SOUND" && w.Detail == "FxSound not installed");
    }

    [Fact]
    public void AMonitorThatIsNoLongerAttachedWarns()
    {
        PlayPreflight check = PlayPreflight.Build(
            Slot(),
            targetExists: true,
            displayExists: true,
            audioExists: true,
            crosshairExists: true,
            audioInstalled: true,
            monitorResolved: false);

        Assert.True(check.CanLaunch);
        Assert.Contains(check.Warnings, w => w.Label == "SCREEN" && w.Detail == "monitor not connected");
    }

    [Fact]
    public void APartialProfileDoesNotClaimTheMissingHalf()
    {
        HotkeySlot slot = Slot();
        slot.AudioPresetId = null;

        PlayPreflight check = PlayPreflight.Build(
            slot,
            targetExists: true,
            displayExists: true,
            audioExists: false,
            crosshairExists: true,
            audioInstalled: true,
            monitorResolved: true);

        Assert.Equal("screen + crosshair", check.Summary);
        Assert.DoesNotContain("SOUND", check.Summary);
    }
}
