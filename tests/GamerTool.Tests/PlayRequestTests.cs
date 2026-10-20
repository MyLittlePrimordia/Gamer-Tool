using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The two ways a play request arrives: a shortcut's command line and the local
/// window message a second launch sends to the running copy.
/// </summary>
public class PlayRequestTests
{
    [Fact]
    public void ReadsTheSpaceForm()
    {
        Assert.True(PlayRequest.TryParse(new[] { "--tray", "--play", "slot_tactical" }, out string id));
        Assert.Equal("slot_tactical", id);
    }

    [Fact]
    public void ReadsTheEqualsForm()
    {
        Assert.True(PlayRequest.TryParse(new[] { "--play=slot_royale" }, out string id));
        Assert.Equal("slot_royale", id);
    }

    [Fact]
    public void IsNotConfusedByALookalikeArgument()
    {
        Assert.False(PlayRequest.TryParse(new[] { "--player", "slot_tactical" }, out string id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void RejectsARequestWithNoSlot()
    {
        Assert.False(PlayRequest.TryParse(new[] { "--play" }, out _));
        Assert.False(PlayRequest.TryParse(new[] { "--play=" }, out _));
        Assert.False(PlayRequest.TryParse(new[] { "--play", "   " }, out _));
    }

    [Fact]
    public void RejectsASlotIdThatWouldBreakTheMessage()
    {
        Assert.False(PlayRequest.TrySlotId("slot_one\nslot_two", out _));
        Assert.False(PlayRequest.TrySlotId("slot\0one", out _));
        Assert.False(PlayRequest.TrySlotId(new string('a', PlayRequest.MaxSlotIdLength + 1), out _));
    }

    [Fact]
    public void AcceptsTheLongestAllowedSlotId()
    {
        string id = new('a', PlayRequest.MaxSlotIdLength);
        Assert.True(PlayRequest.TrySlotId(id, out string parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void ThePayloadRoundTripsWithTheNullTerminatorTheNativeStringHas()
    {
        string payload = PlayRequest.Payload("slot_story") + "\0";

        Assert.True(PlayRequest.TryReadPayload(payload, out string id));
        Assert.Equal("slot_story", id);
    }

    [Fact]
    public void ThePayloadDoesNotAcceptAnotherApp()
    {
        Assert.False(PlayRequest.TryReadPayload("GamerTool.Other\nslot_story", out _));
        Assert.False(PlayRequest.TryReadPayload("slot_story", out _));
        Assert.False(PlayRequest.TryReadPayload(null, out _));
    }

    [Fact]
    public void AShortcutStartsInTheTrayAndCarriesTheSlot()
    {
        Assert.Equal("--tray --play slot_tactical", PlayRequest.ShortcutArguments("slot_tactical"));
    }

    [Fact]
    public void AShortcutQuotesAnIdWithASpace()
    {
        Assert.Equal("\"slot tactical\"", PlayRequest.Quote("slot tactical"));
        Assert.Equal("slot_tactical", PlayRequest.Quote("slot_tactical"));
    }
}
