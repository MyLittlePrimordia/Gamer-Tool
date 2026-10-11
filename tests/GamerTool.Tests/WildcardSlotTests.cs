using System.Collections.Generic;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The wildcard slot: a slot that is not bound to any program.
/// <para>
/// The case it exists for is forgetting to bind something. Binding by path is
/// exact, and that exactness is what makes it safe - and it is also what makes it
/// fail silently when a game updates and moves, which is the one thing a slot is
/// supposed to prevent.
/// </para>
/// <para>
/// Nearly everything here is about not making it fire when it should not. A
/// wildcard that applies on every foreground change would take over the picture of
/// every window the user alt-tabs to, which is worse than not shipping the feature
/// at all. So the tests below concentrate on the ordering (a bound game beats the
/// wildcard), the uniqueness (two wildcards would be decided by list order), and
/// the claims about what does not get copied.
/// </para>
/// </summary>
public class WildcardSlotTests
{
    private static HotkeySlot Slot(string id, string? exe, bool any = false, bool self = false)
    {
        return new HotkeySlot
        {
            Id = id,
            Name = id.ToUpperInvariant(),
            DisplayPresetId = "camper",
            AppExePath = exe,
            AppName = exe is null ? null : System.IO.Path.GetFileNameWithoutExtension(exe),
            AutoActivate = true,
            IsAnyGameTarget = any,
            IsSelfTarget = self
        };
    }

    /// <summary>
    /// A bound game always beats the wildcard.
    /// <para>
    /// The ordering is the whole design. The wildcard is a fallback for "I forgot
    /// to bind this", not a competitor - so if it were checked first, every bound
    /// game in the list would be at the mercy of where the wildcard happened to sit.
    /// </para>
    /// </summary>
    [Fact]
    public void A_bound_game_is_matched_in_preference_to_the_wildcard()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("wild", null, any: true),
            Slot("bound", @"C:\Games\game.exe")
        };

        HotkeySlot? matched = SlotService.MatchForeground(slots, @"C:\Games\game.exe", "game");

        Assert.NotNull(matched);
        Assert.Equal("bound", matched!.Id);
    }

    /// <summary>
    /// And the reverse: the wildcard is only reached when nothing else matched.
    /// </summary>
    [Fact]
    public void The_wildcard_is_not_reached_while_a_bound_game_matches()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("wild", null, any: true),
            Slot("bound", @"C:\Games\game.exe")
        };

        // MatchProcessName is the launch route. It must ignore the wildcard for
        // the same reason, or a bound game launching would be announced twice -
        // once by the watcher and once by the wildcard.
        HotkeySlot? matched = SlotService.MatchProcessName(slots, "game");

        Assert.NotNull(matched);
        Assert.Equal("bound", matched!.Id);
    }

    [Fact]
    public void The_wildcard_is_found_when_nothing_else_matches()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("bound", @"C:\Games\known.exe"),
            Slot("wild", null, any: true)
        };

        HotkeySlot? matched = SlotService.MatchWildcard(slots);

        Assert.NotNull(matched);
        Assert.Equal("wild", matched!.Id);
    }

    /// <summary>
    /// An unarmed wildcard is not a wildcard.
    /// <para>
    /// The dropdown arms it, but a user can turn auto-apply off on a slot the way
    /// they can for any other. Resolving it anyway would mean the feature had no
    /// off switch at all.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unarmed_wildcard_is_not_matched()
    {
        HotkeySlot wild = Slot("wild", null, any: true);
        wild.AutoActivate = false;

        Assert.Null(SlotService.MatchWildcard(new List<HotkeySlot> { wild }));
    }

    [Fact]
    public void A_disabled_wildcard_is_not_matched()
    {
        HotkeySlot wild = Slot("wild", null, any: true);
        wild.Enabled = false;

        Assert.Null(SlotService.MatchWildcard(new List<HotkeySlot> { wild }));
    }

    /// <summary>
    /// A wildcard with nothing to apply is not a wildcard.
    /// <para>
    /// Same rule as every other slot: an empty one cannot do anything, and
    /// ResolveAutoClaims stands its auto-apply down.
    /// </para>
    /// </summary>
    [Fact]
    public void A_wildcard_with_nothing_to_apply_is_not_matched()
    {
        HotkeySlot wild = Slot("wild", null, any: true);
        wild.DisplayPresetId = null;
        wild.AudioPresetId = null;

        Assert.Null(SlotService.MatchWildcard(new List<HotkeySlot> { wild }));
    }

    /// <summary>
    /// Only one wildcard is ever armed.
    /// <para>
    /// Two would both match every unbound fullscreen program, so which one applied
    /// would come down to the order they happen to sit in the list - which is not a
    /// rule anybody could state, let alone rely on. First wins, the same rule every
    /// other conflict on that screen already uses.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_one_wildcard_is_ever_armed()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("first", null, any: true),
            Slot("second", null, any: true)
        };

        SlotService.ResolveAutoClaims(slots);

        Assert.True(slots[0].AutoActivate);
        Assert.False(slots[1].AutoActivate);
    }

    /// <summary>
    /// A bound game is not stood down by the wildcard taking the claim.
    /// <para>
    /// The wildcard's key is "any", which is a different namespace from every
    /// game's name, so it must not collide with one. A wildcard that took a bound
    /// game's slot down would be far worse than useless.
    /// </para>
    /// </summary>
    [Fact]
    public void A_wildcard_does_not_displace_a_bound_game()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("wild", null, any: true),
            Slot("bound", @"C:\Games\game.exe")
        };

        SlotService.ResolveAutoClaims(slots);

        Assert.True(slots[0].AutoActivate);
        Assert.True(slots[1].AutoActivate);
    }

    /// <summary>
    /// The watcher watches a fixed list of names, so a wildcard contributes none.
    /// <para>
    /// Not an oversight. The wildcard works from the foreground window, which is a
    /// different route through the watcher entirely - adding a sentinel name here
    /// would just make the watcher try to match a process called "any".
    /// </para>
    /// </summary>
    [Fact]
    public void A_wildcard_names_no_process_for_the_watcher()
    {
        List<HotkeySlot> slots = new()
        {
            Slot("wild", null, any: true),
            Slot("bound", @"C:\Games\game.exe")
        };

        HashSet<string> names = SlotService.TargetProcessNames(slots);

        Assert.DoesNotContain("any", names);
        Assert.Contains("game", names);
    }

    /// <summary>
    /// Two wildcards are the same target, so duplicating one would be ambiguous.
    /// </summary>
    [Fact]
    public void A_wildcard_is_not_copied_by_a_duplicate()
    {
        HotkeySlot source = Slot("wild", null, any: true);

        HotkeySlot copy = SlotService.Duplicate(source, index: 3);

        Assert.True(source.IsAnyGameTarget);
        Assert.False(copy.IsAnyGameTarget);

        // And the copy has not claimed anything, so it cannot fire on its own.
        Assert.False(copy.HasTarget);
    }

    /// <summary>
    /// A wildcard is a target, so the row reads as aimed at something.
    /// <para>
    /// Without this it reads NO GAME, because it has no path and no name - which is
    /// the symptom a user would report as the slot being broken.
    /// </para>
    /// </summary>
    [Fact]
    public void A_wildcard_reads_as_targeted_on_its_row()
    {
        HotkeySlot wild = Slot("wild", null, any: true);

        Assert.True(wild.HasTarget);
        Assert.Equal("ANY GAME", wild.TargetText);
    }

    /// <summary>
    /// A start-up slot keeps its own label rather than being swallowed by the
    /// wildcard's new case in TargetText.
    /// </summary>
    [Fact]
    public void A_start_up_slot_still_reads_as_gamer_tool()
    {
        HotkeySlot self = Slot("self", null, self: true);

        Assert.Equal("GAMER TOOL", self.TargetText);
    }

    /// <summary>
    /// The three modes are mutually exclusive, and choosing one clears the others.
    /// <para>
    /// They are read as independent flags by HasTarget, TargetText and the matching
    /// code, so a slot carrying two of them would arm two different mechanisms and
    /// revert through whichever happened to be checked first. Pinned here because
    /// the clearing happens in three separate places in the dropdown handler and
    /// nothing about the model itself prevents the combination.
    /// </para>
    /// </summary>
    [Fact]
    public void The_modes_have_separate_keys_so_they_cannot_be_confused()
    {
        HotkeySlot wild = Slot("wild", null, any: true);
        HotkeySlot self = Slot("self", null, self: true);

        Assert.NotEqual(SlotService.TargetKey(wild), SlotService.TargetKey(self));
        Assert.Equal("any", SlotService.TargetKey(wild));
        Assert.Equal("self", SlotService.TargetKey(self));
    }

    /// <summary>
    /// A file carrying two target modes at once is reduced to one.
    /// <para>
    /// The three modes are read as independent flags everywhere else - HasTarget,
    /// TargetText, both matchers - so a slot holding two would arm two mechanisms
    /// and revert through whichever happened to be checked first. A hand-edited file
    /// or a build where the flags were separate is enough to produce one, and the
    /// migration is the only place every saved slot passes through.
    /// </para>
    /// </summary>
    [Fact]
    public void A_slot_carrying_two_modes_is_reduced_to_one_on_load()
    {
        HotkeySlot both = Slot("both", null, any: true);
        both.IsSelfTarget = true;
        both.ApplyOnStart = true;

        AppSettings settings = new();
        settings.Slots.Add(both);

        HotkeySlot migrated = Assert.Single(SlotService.Migrate(settings));

        // Self wins: it does not compete for games at all, so it is the most
        // specific claim of the three.
        Assert.True(migrated.IsSelfTarget);
        Assert.False(migrated.IsAnyGameTarget);
    }

    /// <summary>
    /// The wildcard and on-start cannot both be armed after a load, whichever way
    /// round the file carried them.
    /// </summary>
    [Fact]
    public void The_wildcard_and_on_start_cannot_both_survive_a_load()
    {
        HotkeySlot wild = Slot("wild", null, any: true);
        wild.ApplyOnStart = true;
        wild.IsSelfTarget = false;

        AppSettings settings = new();
        settings.Slots.Add(wild);

        HotkeySlot migrated = Assert.Single(SlotService.Migrate(settings));

        Assert.True(migrated.IsAnyGameTarget);
        Assert.False(migrated.ApplyOnStart);
    }

    /// <summary>
    /// A wildcard survives a restore.
    /// <para>
    /// It names no file, so the repair pass - which exists to stop a backup
    /// pointing at a program that has been uninstalled - had nothing to check and
    /// fell into its empty-path branch, disarming the slot. Every restored backup
    /// would quietly lose it. That is the exact failure the repair exists to
    /// prevent, happening to the one target shape it did not know about.
    /// </para>
    /// </summary>
    [Fact]
    public void A_wildcard_survives_the_restore_repair()
    {
        AppSettings settings = new();
        settings.Slots.Add(Slot("wild", null, any: true));

        var backups = new BackupService();
        backups.Repair(
            settings,
            new List<string>(),
            new List<string>(),
            new List<AppCandidate>(),
            AudioService.DefaultFxSoundPath,
            new RestoreReport());

        Assert.True(Assert.Single(settings.Slots).IsAnyGameTarget);
        Assert.True(Assert.Single(settings.Slots).AutoActivate);
    }
}