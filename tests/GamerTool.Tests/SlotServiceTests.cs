using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Slot migration. This is the code that decides what a new user sees and what
/// every later launch loads, so its behaviour is pinned here.
/// </summary>
public class SlotServiceTests
{
    private static AppSettings FreshSettings() => new();

    [Fact]
    public void Fresh_install_gets_the_six_shipped_slots()
    {
        List<HotkeySlot> slots = SlotService.Migrate(FreshSettings());

        // The documented set, in the documented order. A fresh install used to
        // get four legacy combo slots instead, and never the neutral one.
        Assert.Equal(
            new[] { "Tactical FPS", "Battle Royale", "Story and RPG", "Cinema", "Late Night", "Desktop" },
            slots.Select(s => s.Name));

        Assert.Equal(6, slots.Count);
    }

    [Fact]
    public void Fresh_install_includes_the_neutral_desktop_slot()
    {
        List<HotkeySlot> slots = SlotService.Migrate(FreshSettings());

        HotkeySlot desktop = Assert.Single(slots, s => s.Name == "Desktop");

        // The way back to neutral without hunting for a reset button.
        Assert.Equal("flat", desktop.DisplayPresetId);
        Assert.Equal("flat", desktop.AudioPresetId);
        Assert.True(desktop.HasWork);
        Assert.False(desktop.AutoActivate);
    }

    [Fact]
    public void Fresh_install_gives_every_slot_a_unique_key()
    {
        List<HotkeySlot> slots = SlotService.Migrate(FreshSettings());

        List<string> keys = slots
            .Where(s => !string.IsNullOrWhiteSpace(s.Hotkey))
            .Select(s => HotkeyService.Normalise(s.Hotkey))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        AppSettings settings = FreshSettings();

        List<HotkeySlot> first = SlotService.Migrate(settings);
        settings.Slots = first;
        List<HotkeySlot> second = SlotService.Migrate(settings);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(s => s.Id), second.Select(s => s.Id));
    }

    [Fact]
    public void App_profiles_do_not_multiply_slots_on_every_launch()
    {
        AppSettings settings = FreshSettings();
        settings.AppProfiles.Add(new AppProfile
        {
            Id = "app_1",
            Name = "Counter-Strike 2",
            ExePath = @"C:\Games\cs2.exe",
            ProcessName = "cs2",
            Enabled = true
        });
        settings.AppProfiles.Add(new AppProfile
        {
            Id = "app_2",
            Name = "Apex",
            ExePath = @"D:\Apex.exe",
            ProcessName = "Apex",
            Enabled = true
        });

        // Three launches in a row, which is what used to double the list each time.
        for (int launch = 1; launch <= 3; launch++)
        {
            settings.Slots = SlotService.Migrate(settings);
        }

        // Six shipped slots plus exactly one generated slot per profile.
        Assert.Equal(8, settings.Slots.Count);
        Assert.Equal(2, settings.Slots.Count(s => s.AppExePath is not null));
    }

    [Fact]
    public void Slot_ids_are_dropped_when_they_collide()
    {
        AppSettings settings = FreshSettings();
        settings.Slots.Add(new HotkeySlot { Id = "dup", Name = "One" });
        settings.Slots.Add(new HotkeySlot { Id = "dup", Name = "Two" });

        List<HotkeySlot> slots = SlotService.Migrate(settings);

        Assert.Equal("One", Assert.Single(slots).Name);
    }

    [Fact]
    public void Auto_claims_are_resolved_so_one_slot_owns_a_target()
    {
        AppSettings settings = FreshSettings();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "a",
            Name = "A",
            DisplayPresetId = "flat",
            AppExePath = @"C:\Games\cs2.exe",
            AppName = "cs2",
            AutoActivate = true
        });
        settings.Slots.Add(new HotkeySlot
        {
            Id = "b",
            Name = "B",
            AudioPresetId = "footstep",
            AppExePath = @"C:\Games\cs2.exe",
            AppName = "cs2",
            AutoActivate = true
        });

        List<HotkeySlot> slots = SlotService.Migrate(settings);

        Assert.Equal(2, slots.Count);
        Assert.Equal(1, slots.Count(s => s.AutoActivate));
    }

    [Fact]
    public void Auto_is_turned_off_for_a_slot_with_nothing_to_load()
    {
        AppSettings settings = FreshSettings();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "empty",
            Name = "Empty",
            AppExePath = @"C:\Games\cs2.exe",
            AppName = "cs2",
            AutoActivate = true
        });

        Assert.False(Assert.Single(SlotService.Migrate(settings)).AutoActivate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Auto_is_turned_off_without_a_target(string? exePath)
    {
        AppSettings settings = FreshSettings();
        settings.Slots.Add(new HotkeySlot
        {
            Id = "x",
            Name = "X",
            DisplayPresetId = "flat",
            AppExePath = exePath,
            AutoActivate = true
        });

        Assert.False(Assert.Single(SlotService.Migrate(settings)).AutoActivate);
    }

    [Fact]
    public void Match_foreground_prefers_an_exact_path_over_a_process_name()
    {
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "a", Name = "A", AudioPresetId = "footstep", AppExePath = @"C:\Other\cs2.exe", AppName = "cs2", AutoActivate = true },
            new HotkeySlot { Id = "b", Name = "B", AudioPresetId = "royale", AppExePath = @"C:\Games\cs2.exe", AppName = "cs2", AutoActivate = true }
        };

        Assert.Equal("B", SlotService.MatchForeground(slots, @"C:\Games\cs2.exe", "cs2")?.Name);
    }

    [Fact]
    public void A_disabled_slot_never_matches()
    {
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "a", Name = "A", AudioPresetId = "footstep", AppExePath = @"C:\Games\cs2.exe", AppName = "cs2", AutoActivate = true, Enabled = false }
        };

        Assert.Null(SlotService.MatchForeground(slots, @"C:\Games\cs2.exe", "cs2"));
        Assert.Null(SlotService.MatchProcessName(slots, "cs2"));
    }

    [Fact]
    public void Target_process_names_only_lists_armed_slots()
    {
        List<HotkeySlot> slots = new()
        {
            new HotkeySlot { Id = "a", Name = "A", AudioPresetId = "footstep", AppExePath = @"C:\a\cs2.exe", AppName = "A", AutoActivate = true, Enabled = true },
            new HotkeySlot { Id = "b", Name = "B", AudioPresetId = "royale", AppExePath = @"C:\b\Apex.exe", AppName = "B", AutoActivate = false, Enabled = true }
        };

        HashSet<string> names = SlotService.TargetProcessNames(slots);

        Assert.Contains("A", names);
        Assert.Contains("cs2", names);
        Assert.DoesNotContain("B", names);
    }

    // ---------- the slot key toggle ----------

    [Fact]
    public void A_fully_set_slot_toggles_off_when_it_is_the_one_loaded()
    {
        HotkeySlot slot = new() { DisplayPresetId = "camper", AudioPresetId = "footstep" };

        Assert.True(SlotService.IsLoaded(slot, "camper", "footstep"));
        Assert.False(SlotService.IsLoaded(slot, "night", "footstep"));
        Assert.False(SlotService.IsLoaded(slot, "camper", "royale"));
    }

    [Fact]
    public void A_sound_only_slot_can_still_be_toggled_off()
    {
        // The case that could never work: there is no screen preset to compare
        // against, so the test used to be unsatisfiable and the key could only
        // ever load the slot again.
        HotkeySlot slot = new() { DisplayPresetId = null, AudioPresetId = "footstep" };

        Assert.True(SlotService.IsLoaded(slot, "camper", "footstep"));
        Assert.False(SlotService.IsLoaded(slot, "camper", "royale"));
    }

    [Fact]
    public void A_screen_only_slot_can_still_be_toggled_off()
    {
        HotkeySlot slot = new() { DisplayPresetId = "camper", AudioPresetId = null };

        Assert.True(SlotService.IsLoaded(slot, "camper", "royale"));
        Assert.False(SlotService.IsLoaded(slot, "night", "royale"));
    }

    [Fact]
    public void A_slot_with_nothing_set_is_never_the_loaded_one()
    {
        // It has no work, so the caller turns it away before this is reached,
        // but it must not claim to be loaded if it ever is.
        HotkeySlot slot = new();

        Assert.False(SlotService.IsLoaded(slot, string.Empty, string.Empty));
    }

    [Fact]
    public void A_saved_binding_that_cannot_be_pressed_is_dropped_on_load()
    {
        // Builds before the Alt fix could persist "ALT+SYSTEM". It renders like a
        // chord and never fires, and there is nothing in the UI that would tell
        // the user which slots were real, so it is cleared at load instead.
        AppSettings settings = new()
        {
            Slots = new List<HotkeySlot>
            {
                new() { Id = "slot_a", Name = "Tactical Shooter", Hotkey = "ALT+SYSTEM" },
                new() { Id = "slot_b", Name = "Battle Royale", Hotkey = "CTRL+1" },
                new() { Id = "slot_c", Name = "Cinematic", Hotkey = "" }
            }
        };

        List<HotkeySlot> slots = SlotService.Migrate(settings);

        Assert.Equal(string.Empty, slots[0].Hotkey);
        Assert.Equal("CTRL+1", slots[1].Hotkey);
        Assert.Equal(string.Empty, slots[2].Hotkey);
    }

    [Fact]
    public void Real_bindings_survive_a_migration()
    {
        // The guard above must not eat anything that works, including the
        // numpad, the Win key, and a bare F-key.
        AppSettings settings = new()
        {
            Slots = new List<HotkeySlot>
            {
                new() { Id = "slot_a", Name = "A", Hotkey = "ALT+1" },
                new() { Id = "slot_b", Name = "B", Hotkey = "NUM5" },
                new() { Id = "slot_c", Name = "C", Hotkey = "WIN+Q" },
                new() { Id = "slot_d", Name = "D", Hotkey = "F2" },
                new() { Id = "slot_e", Name = "E", Hotkey = "CTRL+ALT+S" }
            }
        };

        List<HotkeySlot> slots = SlotService.Migrate(settings);

        Assert.Equal(
            new[] { "ALT+1", "NUM5", "WIN+Q", "F2", "CTRL+ALT+S" },
            slots.Select(s => s.Hotkey));
    }
[Fact]
    public void A_profile_whose_slots_were_all_deleted_keeps_them_deleted()
    {
        // The other half of "fresh install gets the six shipped slots", and it used
        // to fail it. An empty list on a used profile is a decision somebody made;
        // treating it as a first run handed back six slots and their default
        // bindings, which then registered with Windows.
        AppSettings settings = new();
        settings.Slots = new List<HotkeySlot>();

        Assert.Empty(SlotService.Migrate(settings, brandNew: false));
    }

    [Fact]
    public void A_used_profile_with_slots_still_keeps_them()
    {
        AppSettings settings = new();
        settings.Slots = new List<HotkeySlot>
        {
            new HotkeySlot { Id = "a", Name = "Mine", Hotkey = "CTRL+ALT+S" },
        };

        List<HotkeySlot> slots = SlotService.Migrate(settings, brandNew: false);

        Assert.Equal("Mine", Assert.Single(slots).Name);
    }
}