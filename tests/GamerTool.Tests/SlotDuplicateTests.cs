using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Copying a slot.
/// <para>
/// Setting up a second slot for a game that already has one means pointing at
/// the same screen, sound, monitor and game four more times. The copy carries
/// those over and nothing else, and the "nothing else" is the part worth pinning:
/// each of the three fields left behind breaks something if it is copied.
/// </para>
/// </summary>
public class SlotDuplicateTests
{
    private static HotkeySlot Source()
    {
        return new HotkeySlot
        {
            Id = "slot_original",
            Name = "Main",
            BuiltIn = true,
            DisplayPresetId = "camper",
            AudioPresetId = "footstep",
            Hotkey = "CTRL+SHIFT+1",
            AppExePath = @"C:\Games\game.exe",
            AppName = "Game",
            AutoActivate = true,
            ApplyOnStart = true,
            MonitorDevice = @"\\.\DISPLAY2",
            IsSelfTarget = true,
            Enabled = true
        };
    }

    [Fact]
    public void The_things_that_are_annoying_to_re_type_come_across()
    {
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 4);

        Assert.Equal("camper", copy.DisplayPresetId);
        Assert.Equal("footstep", copy.AudioPresetId);
        Assert.Equal(@"\\.\DISPLAY2", copy.MonitorDevice);
        Assert.True(copy.ApplyOnStart);
        Assert.True(copy.Enabled);
        Assert.Equal("Main copy", copy.Name);
    }

    [Fact]
    public void The_key_is_not_copied()
    {
        // RegisterHotKey fails on a duplicate and the slot is silently dead. The
        // copy therefore starts unbound and the user presses a key for it.
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 4);

        Assert.Equal(string.Empty, copy.Hotkey);
    }

    [Fact]
    public void The_target_is_not_copied()
    {
        // Two slots auto claiming one game is exactly the ambiguity the foreground
        // matcher and the auto-claim resolver have to unpick. A duplicate would
        // double it on every launch.
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 4);

        Assert.False(copy.IsSelfTarget);
        Assert.True(string.IsNullOrEmpty(copy.AppExePath));
        Assert.True(string.IsNullOrEmpty(copy.AppName));

        // And so it cannot claim anything by itself until it is asked to.
        Assert.False(copy.AutoActivate);
        Assert.False(copy.HasTarget);
    }

    [Fact]
    public void The_copy_is_the_users_own_and_not_a_built_in()
    {
        // A built-in keeps its name and cannot be renamed or deleted. A copy has
        // to be neither of those things to be useful.
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 4);

        Assert.False(copy.BuiltIn);
    }

    [Fact]
    public void The_copy_gets_its_own_id()
    {
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 4);

        Assert.NotEqual("slot_original", copy.Id);
        Assert.False(string.IsNullOrWhiteSpace(copy.Id));
    }

    [Fact]
    public void A_copy_never_collides_with_another_slot()
    {
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 4);
        HotkeySlot copyOfCopy = SlotService.Duplicate(copy, index: 5);

        Assert.NotEqual(copy.Id, copyOfCopy.Id);
        Assert.Equal(2, new[] { copy.Id, copyOfCopy.Id }.Distinct().Count());
    }

    [Fact]
    public void Duplicating_does_not_change_the_original()
    {
        HotkeySlot source = Source();

        SlotService.Duplicate(source, index: 4);

        Assert.Equal("slot_original", source.Id);
        Assert.Equal("Main", source.Name);
        Assert.Equal("CTRL+SHIFT+1", source.Hotkey);
        Assert.True(source.BuiltIn);
        Assert.True(source.IsSelfTarget);
    }

    [Fact]
    public void An_empty_slot_copies_into_something_named()
    {
        HotkeySlot blank = new() { Id = "slot_blank", Name = "   " };

        HotkeySlot copy = SlotService.Duplicate(blank, index: 2);

        Assert.False(string.IsNullOrWhiteSpace(copy.Name));
    }

    [Fact]
    public void Copies_survive_the_migration_that_pins_down_ids_and_keys()
    {
        // What happens on the next launch: the copy has no key, and the migration
        // must leave it that way rather than inventing a binding.
        AppSettings settings = new();
        settings.Slots.Add(Source());
        settings.Slots.Add(SlotService.Duplicate(Source(), index: 2));

        List<HotkeySlot> migrated = SlotService.Migrate(settings);

        Assert.Equal(2, migrated.Count);
        Assert.All(migrated, s => Assert.NotEqual(string.Empty, s.Id));
        Assert.Equal(2, migrated.Select(s => s.Id).Distinct().Count());
        Assert.Equal(string.Empty, migrated[1].Hotkey);
    }

    [Fact]
    public void Two_copies_never_end_up_sharing_one_hotkey()
    {
        AppSettings settings = new();
        settings.Slots.Add(Source());
        HotkeySlot copy = SlotService.Duplicate(Source(), index: 2);

        // The user binds the copy by hand; the pair is then valid.
        copy.Hotkey = "CTRL+SHIFT+2";
        settings.Slots.Add(copy);

        List<HotkeySlot> migrated = SlotService.Migrate(settings);

        Assert.Equal(2, migrated.Count);
        Assert.Equal(
            migrated.Select(s => s.Hotkey).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().Count(),
            migrated.Count(s => !string.IsNullOrWhiteSpace(s.Hotkey)));
    }
}
