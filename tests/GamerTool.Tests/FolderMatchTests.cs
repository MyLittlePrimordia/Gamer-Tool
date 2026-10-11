using System;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Binding a slot by folder.
/// <para>
/// The third way to say what a slot is for, after an exact exe path and a process
/// name. It exists for the installs where neither works: Ubisoft Connect and the EA
/// app register a folder rather than an executable, and a game behind a launcher
/// has its exe somewhere no scan looks.
/// </para>
/// </summary>
public class FolderMatchTests
{
    [Theory]
    [InlineData(@"C:\Games\Witcher 3\bin\x64\witcher3.exe", @"C:\Games\Witcher 3", true)]
    [InlineData(@"D:\Games\Foo\bar.exe", @"D:\Games\Foo", true)]
    [InlineData(@"D:\Games\Foo\bar.exe", @"D:\Games\Foo\", true)]
    [InlineData(@"D:\Games\Foo\bin\deep\bar.exe", @"D:\Games\Foo", true)]
    public void AnExecutableInsideTheFolderMatches(string exe, string folder, bool expected)
    {
        Assert.Equal(expected, SlotService.IsUnderFolder(exe, folder));
    }

    [Theory]
    [InlineData(@"D:\Games\FooBar\x.exe", @"D:\Games\Foo")]
    [InlineData(@"D:\Games\FooBar\x.exe", @"D:\Games\Foo\")]
    public void ASiblingFolderWithASharedPrefixDoesNotMatch(string exe, string folder)
    {
        // The whole implementation is the trailing separator, and this is what it
        // is for. Without it D:\Games\Foo claims every game on the drive that
        // happens to start with those letters.
        Assert.False(SlotService.IsUnderFolder(exe, folder));
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe", @"C:\")]
    [InlineData(@"C:\Windows\notepad.exe", @"C:\\")]
    public void ADriveRootIsNotAFolderRule(string exe, string folder)
    {
        // "Everything under C:\" is not a folder rule, it is the machine. A profile
        // carrying it would fire on everything the user ever runs, which for this
        // app means a game loading its boost every time.
        Assert.False(SlotService.IsUnderFolder(exe, folder));
    }

    [Fact]
    public void TheFolderItselfIsNotAMatch()
    {
        // The rule is "runs out of here". A path equal to the folder is a
        // directory, not something that can be executed.
        Assert.False(SlotService.IsUnderFolder(@"D:\Games\Foo", @"D:\Games\Foo"));
    }

    [Theory]
    [InlineData(null, @"D:\Games\Foo")]
    [InlineData("", @"D:\Games\Foo")]
    [InlineData("   ", @"D:\Games\Foo")]
    [InlineData(@"D:\Games\x.exe", null)]
    [InlineData(@"D:\Games\x.exe", "")]
    public void NothingOnOneSideMeansNoMatch(string? exe, string? folder)
    {
        Assert.False(SlotService.IsUnderFolder(exe!, folder!));
    }

    [Theory]
    [InlineData("not a path at all \0 broken", @"D:\Games")]
    [InlineData(@"D:\Games\x.exe", "Z:\\bad|path")]
    [InlineData("", "")]
    public void RubbishInTheProfileDoesNotThrow(string exe, string folder)
    {
        // This is reached from the foreground tick. An exception here takes the
        // whole tick down, so a hand-edited settings file must not be able to do it.
        Assert.False(SlotService.IsUnderFolder(exe, folder));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // Windows paths are not case sensitive and the profile is written by a
        // human at least as often as by this app.
        Assert.True(SlotService.IsUnderFolder(@"D:\GAMES\foo\bar.exe", @"d:\games\Foo"));
    }

    [Fact]
    public void AFolderRuleMatchesInTheForeground()
    {
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot
            {
                Id = "1",
                Name = "Witcher",
                TargetFolder = @"D:\Games\Witcher 3",
                DisplayPresetId = "flat",
                AudioPresetId = "flat",
                AutoActivate = true
            }
        };

        Assert.NotNull(SlotService.MatchForeground(slots, @"D:\Games\Witcher 3\bin\witcher3.exe", "witcher3"));
        Assert.Null(SlotService.MatchForeground(slots, @"D:\Games\Something Else\game.exe", "game"));
    }

    [Fact]
    public void AnExactPathStillBeatsAFolderRule()
    {
        // Both are live in the same list. A specific binding is a better answer
        // than a broad one, and letting list order decide which wins would make the
        // specific bindings pointless on a machine with a wide folder rule.
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot
            {
                Id = "1",
                Name = "Folder",
                TargetFolder = @"D:\Games",
                DisplayPresetId = "flat",
                AutoActivate = true
            },
            new HotkeySlot
            {
                Id = "2",
                Name = "Exact",
                AppExePath = @"D:\Games\Other\other.exe",
                DisplayPresetId = "flat",
                AutoActivate = true
            }
        };

        Assert.Equal("2", SlotService.MatchForeground(slots, @"D:\Games\Other\other.exe", "other")!.Id);
    }

    [Fact]
    public void AnExactPathListedFirstStillWins()
    {
        // Order independence, which is the same claim from the other direction.
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot
            {
                Id = "1",
                Name = "Exact",
                AppExePath = @"D:\Games\Other\other.exe",
                DisplayPresetId = "flat",
                AutoActivate = true
            },
            new HotkeySlot
            {
                Id = "2",
                Name = "Folder",
                TargetFolder = @"D:\Games",
                DisplayPresetId = "flat",
                AutoActivate = true
            }
        };

        Assert.Equal("1", SlotService.MatchForeground(slots, @"D:\Games\Other\other.exe", "other")!.Id);
    }

    [Fact]
    public void ADisabledFolderRuleDoesNotMatch()
    {
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot
            {
                Id = "1",
                Name = "Off",
                TargetFolder = @"D:\Games",
                DisplayPresetId = "flat",
                AutoActivate = true,
                Enabled = false
            }
        };

        Assert.Null(SlotService.MatchForeground(slots, @"D:\Games\a.exe", "a"));
    }

    [Fact]
    public void AFolderRuleContributesNoProcessNameToTheLaunchScan()
    {
        // Launch and exit detection stays name-based, deliberately: a folder rule
        // says "when something from here is in the foreground", not "watch this
        // drive for processes starting", and the second is not a thing this app
        // does on a timer.
        HotkeySlot slot = new()
        {
            Id = "1",
            Name = "Folder",
            TargetFolder = @"D:\Games\Witcher 3"
        };

        Assert.Empty(SlotService.TargetProcessNames(new[] { slot }));
    }
}