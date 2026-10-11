using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Naming what a preset deletion is about to break.
/// </summary>
/// <remarks>
/// The confirmation already said the preset would come out of "any slot that points
/// at it", which is true and tells the user nothing they can act on. By the time
/// they have read it the decision is made, and afterwards the only evidence is a
/// slot that has quietly gone back to saying NOTHING SET - which reads as the slot
/// having lost its settings on its own.
/// </remarks>
public class PresetUsageTests
{
    private static HotkeySlot Slot(string name, string? display = null, string? audio = null, bool enabled = true) => new()
    {
        Id = name,
        Name = name,
        DisplayPresetId = display,
        AudioPresetId = audio,
        Enabled = enabled
    };

    [Fact]
    public void APresetNoSlotUsesReportsNothing()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("A", display: "other"),
            Slot("B", audio: "unrelated")
        };

        Assert.Empty(SlotService.SlotsUsingPreset(slots, "mine", checkDisplay: true));
        Assert.Equal(string.Empty, SlotService.DescribeUsage(new List<HotkeySlot>()));
    }

    [Fact]
    public void TheScreenAndSoundHalvesAreAskedSeparately()
    {
        // A display preset id and an audio preset id can collide as strings, and the
        // two halves of a slot are independent. Asking about the wrong half would
        // name slots that are not affected and miss the ones that are.
        List<HotkeySlot> slots = new()
        {
            Slot("Screen", display: "shared"),
            Slot("Sound", audio: "shared")
        };

        List<HotkeySlot> screen = SlotService.SlotsUsingPreset(slots, "shared", checkDisplay: true);
        List<HotkeySlot> sound = SlotService.SlotsUsingPreset(slots, "shared", checkDisplay: false);

        Assert.Single(screen);
        Assert.Equal("Screen", screen[0].Name);

        Assert.Single(sound);
        Assert.Equal("Sound", sound[0].Name);
    }

    [Fact]
    public void ADisabledSlotStillCounts()
    {
        // It keeps its preset while disabled, so deleting the preset out from under
        // it means the user finds the slot empty the day they switch it back on, with
        // the deletion long out of mind.
        List<HotkeySlot> slots = new() { Slot("Off", display: "mine", enabled: false) };

        Assert.Single(SlotService.SlotsUsingPreset(slots, "mine", checkDisplay: true));
    }

    [Fact]
    public void AnEmptyPresetIdMatchesNothing()
    {
        // That is what "no preset" is stored as, and it is not a preset anybody can
        // delete. Without this, every slot with the half unset would be named.
        List<HotkeySlot> slots = new()
        {
            Slot("A"),
            Slot("B", display: null),
            Slot("C", display: string.Empty)
        };

        Assert.Empty(SlotService.SlotsUsingPreset(slots, string.Empty, checkDisplay: true));
        Assert.Empty(SlotService.SlotsUsingPreset(slots, null, checkDisplay: true));
        Assert.Empty(SlotService.SlotsUsingPreset(slots, "   ", checkDisplay: true));
    }

    [Fact]
    public void TheLookupIgnoresCase()
    {
        List<HotkeySlot> slots = new() { Slot("A", display: "Mine") };

        Assert.Single(SlotService.SlotsUsingPreset(slots, "mine", checkDisplay: true));
    }

    [Fact]
    public void OneSlotIsNamed()
    {
        Assert.Equal(
            "It is loaded by Cinema.",
            SlotService.DescribeUsage(new List<HotkeySlot> { Slot("Cinema") }));
    }

    [Fact]
    public void TwoSlotsAreBothNamed()
    {
        Assert.Equal(
            "It is loaded by Cinema and Late Night.",
            SlotService.DescribeUsage(new List<HotkeySlot> { Slot("Cinema"), Slot("Late Night") }));
    }

    [Fact]
    public void ThreeOrMoreFallBackToACountWithTheFirstTwoNamed()
    {
        // A dialog is one line. Five slot names in it is not a sentence.
        string sentence = SlotService.DescribeUsage(new List<HotkeySlot>
        {
            Slot("Cinema"), Slot("Late Night"), Slot("Tactical FPS"), Slot("Story and RPG")
        });

        Assert.Equal("It is loaded by 4 slots, including Cinema and Late Night.", sentence);
    }

    [Fact]
    public void AnUnnamedSlotIsDescribedRatherThanLeftBlank()
    {
        // "It is loaded by ." is worse than saying the slot has no name.
        Assert.Equal(
            "It is loaded by an unnamed slot.",
            SlotService.DescribeUsage(new List<HotkeySlot> { Slot(string.Empty) }));
    }

    [Fact]
    public void AnEmptyListDescribesNothingRatherThanSayingZero()
    {
        // So the caller can append it unconditionally and get no sentence at all
        // when there is nothing to say, rather than "It is loaded by 0 slots".
        Assert.Equal(string.Empty, SlotService.DescribeUsage(new List<HotkeySlot>()));
        Assert.Equal(string.Empty, SlotService.DescribeUsage(null!));
    }

    [Fact]
    public void EverySlotIsReturnedRatherThanJustTheFirst()
    {
        // The count in the sentence comes from this list, so returning one would
        // report every busy preset as being used by exactly one slot.
        List<HotkeySlot> slots = new()
        {
            Slot("A", display: "mine"),
            Slot("B", display: "mine"),
            Slot("C", display: "mine")
        };

        Assert.Equal(3, SlotService.SlotsUsingPreset(slots, "mine", checkDisplay: true).Count);
    }

    /// <summary>
    /// Both delete confirmations say what they are about to break.
    /// </summary>
    /// <remarks>
    /// Asserted from the source because the dialog cannot be driven from a test
    /// without a window and a dispatcher. What is being pinned is that the lookup
    /// happens at all, and on the half it belongs to - the screen handler asking
    /// about sound presets would name the wrong slots entirely.
    /// </remarks>
    [Theory]
    [InlineData("MainWindow.Display.cs", "private void OnDeleteScreenClick", "checkDisplay: true")]
    [InlineData("MainWindow.Audio.cs", "private void OnDeleteSoundClick", "checkDisplay: false")]
    public void TheDeleteConfirmationNamesTheSlotsThatUseThePreset(string file, string signature, string half)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "app", file));

        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n    private ", at + signature.Length, System.StringComparison.Ordinal);
        string body = end < 0 ? source[at..] : source[at..end];

        Assert.Contains("SlotService.SlotsUsingPreset(", body, System.StringComparison.Ordinal);
        Assert.Contains(half, body, System.StringComparison.Ordinal);
        Assert.Contains("SlotService.DescribeUsage(", body, System.StringComparison.Ordinal);
    }
}
