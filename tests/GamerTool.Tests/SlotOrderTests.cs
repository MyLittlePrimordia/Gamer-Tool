using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Reordering the slot board.
/// <para>
/// The order is not decoration: the tray menu and the crosshair/slot cycle read the
/// list top to bottom, and the hotkey ids are positions in it. So the two things
/// worth pinning are that a move is a move rather than a swap of neighbours' data,
/// and that the ends refuse instead of wrapping.
/// </para>
/// </summary>
public class SlotOrderTests
{
    private static List<HotkeySlot> Slots() => new()
    {
        new HotkeySlot { Id = "a", Name = "A" },
        new HotkeySlot { Id = "b", Name = "B" },
        new HotkeySlot { Id = "c", Name = "C" },
    };

    [Fact]
    public void Moving_up_puts_a_slot_before_its_neighbour()
    {
        List<HotkeySlot> slots = Slots();

        Assert.True(SlotService.Move(slots, slots[1], -1));
        Assert.Equal(new[] { "b", "a", "c" }, slots.ConvertAll(s => s.Id));
    }

    [Fact]
    public void Moving_down_puts_a_slot_after_its_neighbour()
    {
        List<HotkeySlot> slots = Slots();

        Assert.True(SlotService.Move(slots, slots[1], 1));
        Assert.Equal(new[] { "a", "c", "b" }, slots.ConvertAll(s => s.Id));
    }

    [Fact]
    public void The_ends_refuse_rather_than_wrapping()
    {
        List<HotkeySlot> slots = Slots();

        Assert.False(SlotService.Move(slots, slots[0], -1));
        Assert.False(SlotService.Move(slots, slots[2], 1));
        Assert.Equal(new[] { "a", "b", "c" }, slots.ConvertAll(s => s.Id));
    }

    [Fact]
    public void A_slot_that_is_not_in_the_list_is_left_alone()
    {
        List<HotkeySlot> slots = Slots();

        Assert.False(SlotService.Move(slots, new HotkeySlot { Id = "gone" }, 1));
        Assert.Equal(3, slots.Count);
    }
}
