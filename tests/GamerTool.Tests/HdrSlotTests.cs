using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The slot half of HDR: the field that has to survive duplication and the words
/// that describe what a slot does.
/// </summary>
public class HdrSlotTests
{
    [Fact]
    public void CopyCarriesTheHdrMode()
    {
        HotkeySlot slot = new() { Id = "slot", Name = "Arena", HdrMode = SlotHdrMode.On };

        HotkeySlot copy = slot.Copy();

        Assert.Equal(SlotHdrMode.On, copy.HdrMode);
    }

    [Fact]
    public void DuplicateCarriesTheHdrMode()
    {
        HotkeySlot slot = new() { Id = "slot", Name = "Arena", HdrMode = SlotHdrMode.Off };

        HotkeySlot copy = SlotService.Duplicate(slot, 2);

        Assert.Equal(SlotHdrMode.Off, copy.HdrMode);
    }

    [Fact]
    public void HdrAloneCountsAsWorkAndSaysSo()
    {
        HotkeySlot slot = new() { Id = "slot", Name = "Arena", HdrMode = SlotHdrMode.On };

        Assert.True(slot.HasWork);
        Assert.Equal("HDR ONLY", slot.WorkText);
    }

    [Fact]
    public void LeaveIsNotWorkAndDoesNotAppearInTheSummary()
    {
        HotkeySlot slot = new() { Id = "slot", Name = "Arena" };

        Assert.False(slot.HasWork);
        Assert.Equal("NOTHING SET", slot.WorkText);
    }

    [Fact]
    public void HdrSitsBetweenScreenAndSound()
    {
        HotkeySlot slot = new()
        {
            Id = "slot",
            Name = "Arena",
            DisplayPresetId = "screen",
            AudioPresetId = "sound",
            CrosshairPresetId = "crosshair",
            HdrMode = SlotHdrMode.On,
        };

        Assert.Equal("SCREEN + HDR + SOUND + CROSSHAIR", slot.WorkText);
    }

    [Fact]
    public void MigrationLeavesAValueThisBuildDoesNotKnowAlone()
    {
        AppSettings settings = new()
        {
            Slots = new List<HotkeySlot>
            {
                new() { Id = "slot", Name = "Arena", HdrMode = (SlotHdrMode)99 },
            },
        };

        List<HotkeySlot> migrated = SlotService.Migrate(settings, brandNew: false);

        Assert.Single(migrated);
        Assert.Equal(SlotHdrMode.Leave, migrated[0].HdrMode);
    }
}
