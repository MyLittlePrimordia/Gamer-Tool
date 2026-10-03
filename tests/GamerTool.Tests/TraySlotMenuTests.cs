using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The tray's slot list: what it says, and the swap that replaces it.
/// <para>
/// This menu can load a slot now, which is the only thing it does that the
/// reset lines do not, and it is the surface the user is on when the window is
/// closed. Two things can go wrong and neither is visible in a screenshot of
/// the finished menu: a line that lies about which slot is loaded, and a rebuild
/// that leaves a line for a slot which no longer exists.
/// </para>
/// <para>
/// The list lives behind an arrow rather than in the menu, so these are mostly
/// about the child list and the two gutters that keep the arrow and the tick
/// from being clipped.
/// </para>
/// </summary>
[Collection("wpf")]
public class TraySlotMenuTests
{
    private static List<TraySlotEntry> Slots(params (string Id, string Label, bool Active)[] entries) =>
        entries
            .Select(e => new TraySlotEntry { Id = e.Id, Label = e.Label, Active = e.Active, Detail = "detail" })
            .ToList();

    /// <summary>
    /// A menu with the same shape the tray builds: Open, the slot arrow, the
    /// resets and Quit.
    /// </summary>
    private static ContextMenuStrip BuildMenu(out ToolStripMenuItem slots, out ToolStripSeparator rule)
    {
        ContextMenuStrip menu = DarkTrayMenuRenderer.Menu();
        menu.Items.Add(DarkTrayMenuRenderer.Item("Open", (s, e) => { }));
        rule = DarkTrayMenuRenderer.Separator();
        menu.Items.Add(rule);
        slots = DarkTrayMenuRenderer.Submenu("Slots");
        menu.Items.Add(slots);
        menu.Items.Add(DarkTrayMenuRenderer.Separator());

        // The two toggles, between the slots and the resets. Hidden by default
        // because Available is what Shape reads, and the effects line starts
        // hidden - a machine without FxSound has nothing for it to do.
        ToolStripMenuItem effects = DarkTrayMenuRenderer.CheckableItem(
            "Sound effects", "Tick means the effects are OFF.", false, (s, e) => { });
        effects.Visible = false;
        menu.Items.Add(effects);
        menu.Items.Add(DarkTrayMenuRenderer.CheckableItem(
            "Night filter", "Warm the screen on a schedule", false, (s, e) => { }));

        menu.Items.Add(DarkTrayMenuRenderer.Separator());
        menu.Items.Add(DarkTrayMenuRenderer.Item("Reset Display", (s, e) => { }));
        menu.Items.Add(DarkTrayMenuRenderer.Item("Reset Sound", (s, e) => { }));
        menu.Items.Add(DarkTrayMenuRenderer.Separator());
        menu.Items.Add(DarkTrayMenuRenderer.Item("Quit", (s, e) => { }));
        return menu;
    }

    private static ContextMenuStrip BuildMenu(out ToolStripMenuItem slots) =>
        BuildMenu(out slots, out _);

    /// <summary>
    /// The menu's lines with separators shown as "-" and hidden ones left out,
    /// so a failure reads as what the user sees rather than as what the
    /// collection happens to hold.
    /// </summary>
    private static string?[] Shape(ContextMenuStrip menu) =>
        menu.Items
            .Cast<ToolStripItem>()
            .Where(i => i.Available)
            .Select(i => i is ToolStripSeparator ? "-" : i.Text)
            .ToArray();

    [Fact]
    public void SlotsSitBehindAnArrowRatherThanInTheMenuItself()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);

        TrayService.ReplaceSlotLines(
            slots.DropDownItems, Slots(("a", "Gaming", false), ("b", "Cinema", false)), _ => { });

        // The menu is a fixed number of lines however many slots exist. A user who
        // makes their own can have twenty, and a menu that long either runs off
        // the bottom of the screen or moves Reset and Quit out from under the
        // cursor that was on its way to them.
        //
        // "Sound effects" is absent because BuildMenu hides it, which is what a
        // machine without FxSound sees. "Night filter" is present because it has no
        // such dependency - it is a question about whether the app watches the
        // clock, not about an engine.
        Assert.Equal(
            new[] { "Open", "-", "Slots", "-", "Night filter", "-", "Reset Display", "Reset Sound", "-", "Quit" },
            Shape(menu));

        Assert.Equal(
            new[] { "Gaming", "Cinema" },
            slots.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text));
    }

    [Fact]
    public void TheMenuDoesNotGrowHoweverManySlotsExist()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);
        int before = menu.Items.Count;

        TrayService.ReplaceSlotLines(
            slots.DropDownItems,
            Enumerable.Range(0, 40)
                .Select(i => new TraySlotEntry { Id = "s" + i, Label = "Slot " + i, Detail = "d" })
                .ToList(),
            _ => { });

        Assert.Equal(before, menu.Items.Count);
        Assert.Equal(40, slots.DropDownItems.Count);
    }

    [Fact]
    public void BothGuttersAreThereForTheArrowAndForTheTick()
    {
        using ContextMenuStrip menu = DarkTrayMenuRenderer.Menu();
        using ToolStripMenuItem slots = DarkTrayMenuRenderer.Submenu("Slots");

        // Two strips, two gutters. Without the parent's the arrow is clipped,
        // and without the child's the tick beside the loaded slot has nowhere to
        // go. Touching DropDown is what makes the shell build the child strip
        // at all, so the margin can only be set once it exists.
        Assert.True(menu.ShowImageMargin);

        ToolStripDropDownMenu child = Assert.IsType<ToolStripDropDownMenu>(slots.DropDown);
        Assert.True(child.ShowImageMargin);
        Assert.Equal(TrayColours.Surface, child.BackColor);
        Assert.Equal(TrayColours.Text, slots.ForeColor);
    }

    [Fact]
    public void RebuildingReplacesTheListRatherThanAddingToIt()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);

        TrayService.ReplaceSlotLines(slots.DropDownItems, Slots(("a", "Gaming", false)), _ => { });
        TrayService.ReplaceSlotLines(
            slots.DropDownItems, Slots(("b", "Cinema", false), ("c", "Music", false)), _ => { });

        // "Gaming" gone, not merely pushed down the list. A list that keeps a
        // line for a slot the user has deleted is how you end up choosing a
        // preset that no longer exists and being told a slot name you do not
        // recognise.
        Assert.Equal(
            new[] { "Cinema", "Music" },
            slots.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text));
    }

    [Fact]
    public void TheListCanBeEmptiedAndFilledAgain()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);

        TrayService.ReplaceSlotLines(slots.DropDownItems, Slots(("a", "Gaming", false)), _ => { });
        TrayService.ReplaceSlotLines(slots.DropDownItems, new List<TraySlotEntry>(), _ => { });

        Assert.Empty(slots.DropDownItems);

        // A rebuild after an empty one still lands in the right place, which is
        // the case an index that assumed a non-empty range gets wrong.
        TrayService.ReplaceSlotLines(slots.DropDownItems, Slots(("a", "Gaming", false)), _ => { });
        Assert.Equal("Gaming", slots.DropDownItems.Cast<ToolStripItem>().Single().Text);
    }

    [Fact]
    public void TheLoadedSlotIsTickedAndTheOthersAreNot()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);

        TrayService.ReplaceSlotLines(
            slots.DropDownItems, Slots(("a", "Gaming", false), ("b", "Cinema", true)), _ => { });

        ToolStripMenuItem[] lines = slots.DropDownItems.OfType<ToolStripMenuItem>().ToArray();

        Assert.False(lines.Single(i => i.Text == "Gaming").Checked);
        Assert.True(lines.Single(i => i.Text == "Cinema").Checked);
    }

    [Fact]
    public void ATickedSlotIsNotSomethingToClick()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);

        TrayService.ReplaceSlotLines(slots.DropDownItems, Slots(("a", "Gaming", true)), _ => { });

        ToolStripMenuItem item = slots.DropDownItems.OfType<ToolStripMenuItem>().Single();

        // With CheckOnClick on, choosing the slot already loaded would un-tick
        // it and load it again, where the same choice made with the key would
        // have turned the screen and sound back off. The tick is a statement,
        // not a switch.
        Assert.False(item.CheckOnClick);
    }

    [Fact]
    public void ClickingALineReportsTheSlotNotItsPosition()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);
        List<string> clicked = new();

        TrayService.ReplaceSlotLines(
            slots.DropDownItems,
            Slots(("slot-1", "Gaming", false), ("slot-2", "Cinema", false)),
            clicked.Add);

        // The list is rebuilt on every change, so an index would be stale by
        // the time anyone clicked. The id is the thing that still resolves.
        foreach (ToolStripMenuItem item in slots.DropDownItems.OfType<ToolStripMenuItem>())
        {
            item.PerformClick();
        }

        Assert.Equal(new[] { "slot-1", "slot-2" }, clicked);
    }

    [Fact]
    public void AnEmptySlotListHidesTheArrowAndTheRuleWithIt()
    {
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots, out ToolStripSeparator rule);

        // What SetSlots does, so the rule about the two lines going together is
        // pinned rather than assumed.
        TrayService.ReplaceSlotLines(slots.DropDownItems, new List<TraySlotEntry>(), _ => { });
        bool any = slots.DropDownItems.Count > 0;
        slots.Visible = any;
        rule.Visible = any;

        // A bare "----" under Open, or a "Slots" that opens onto nothing, is a
        // line taking up room and asking a question the answer to is no. With
        // both gone this is the menu the app had before quick-apply existed.
        Assert.False(slots.Visible);
        Assert.False(rule.Visible);
        Assert.Equal(
            new[] { "Open", "-", "Night filter", "-", "Reset Display", "Reset Sound", "-", "Quit" },
            Shape(menu));
    }

    [Fact]
    public void AMonitorScopedSlotSaysWhichScreenItIsFor()
    {
        // The tray is opened while looking at one screen, and a slot can be
        // pointed at another. Six identical names is a list where the wrong one
        // is easy to pick and the mistake only shows on the monitor you are not
        // facing.
        Assert.Equal("Gaming", TrayService.ComposeSlotLabel("Gaming", null));
        Assert.Equal("Gaming", TrayService.ComposeSlotLabel("Gaming", "   "));
        Assert.Equal("Gaming  (DP-2)", TrayService.ComposeSlotLabel("Gaming", "DP-2"));
    }

    [Fact]
    public void AnUnnamedSlotStillHasSomethingToSay()
    {
        Assert.Equal("Unnamed", TrayService.ComposeSlotLabel("", null));
        Assert.Equal("Unnamed", TrayService.ComposeSlotLabel("   ", null));
        Assert.Equal("Unnamed  (DP-2)", TrayService.ComposeSlotLabel(null, "DP-2"));
    }

    [Fact]
    public void ASlotHoverSaysTheKeyWhatItLoadsAndWhere()
    {
        Assert.Equal(
            "Ctrl+Alt+1  ·  SCREEN + SOUND  ·  DP-2",
            TrayService.ComposeSlotDetail("Ctrl+Alt+1", "SCREEN + SOUND", "DP-2"));
    }

    [Fact]
    public void ASlotHoverLeavesOutWhatIsNotSet()
    {
        // A gap where a key should be reads as a broken binding, where an
        // absent one reads as a slot that simply has no key yet.
        Assert.Equal("SCREEN ONLY", TrayService.ComposeSlotDetail(null, "SCREEN ONLY", null));
        Assert.Equal("SOUND ONLY", TrayService.ComposeSlotDetail("   ", "SOUND ONLY", null));
        Assert.Equal("SCREEN ONLY  ·  DP-2", TrayService.ComposeSlotDetail("", "SCREEN ONLY", "DP-2"));
    }

    [Fact]
    public void AnEmptySlotSaysSoRatherThanReadingAsEmpty()
    {
        Assert.Equal("Ctrl+Alt+1  ·  Nothing set", TrayService.ComposeSlotDetail("Ctrl+Alt+1", null, null));
        Assert.Equal("Nothing set", TrayService.ComposeSlotDetail(null, null, null));
        Assert.Equal("Nothing set  ·  DP-2", TrayService.ComposeSlotDetail(null, "  ", "DP-2"));
    }

    [Fact]
    public void AnEmptySlotIsStillListed()
    {
        // Hiding it would make the list change shape as slots are edited, and an
        // empty slot is a slot the user made and has not finished. It says so in
        // its own hover text rather than vanishing.
        using ContextMenuStrip menu = BuildMenu(out ToolStripMenuItem slots);

        TrayService.ReplaceSlotLines(
            slots.DropDownItems,
            new List<TraySlotEntry> { new() { Id = "a", Label = "WIP", Detail = "Nothing set" } },
            _ => { });

        Assert.Equal("WIP", slots.DropDownItems.Cast<ToolStripItem>().Single().Text);
    }
}
