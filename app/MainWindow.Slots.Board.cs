using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;

namespace GamerTool;

// Slot board: adding, moving, duplicating, renaming and resetting a slot, and
// the claim rules that decide which slot owns a game.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    private void OnAddSlotClick(object sender, RoutedEventArgs e)
    {
        _settings.Slots.Add(SlotService.NewSlot(_settings.Slots.Count + 1));
        Commit();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();
    }


    /// <summary>
    /// Points a slot at the game the user was in a moment ago.
    /// </summary>
    /// <remarks>
    /// The awkward half of this feature is that clicking the control takes the game
    /// out of the foreground, so nothing can be read at the moment of the click. The
    /// answer is that the watcher has been remembering it all along, and this reads
    /// that memory rather than asking the machine.
    /// <para>
    /// Adds a row when the game has none, and moves the existing row when it already
    /// has one. Two rows for one game is how a user ends up with a slot they have
    /// forgotten about and no way to tell which of them fires; moving the existing
    /// one also keeps whatever preset pair and hotkey it already carried, which is
    /// the work the user actually did.
    /// </para>
    /// </remarks>
    private void OnAddPlayingClick(object sender, RoutedEventArgs e)
    {
        CurrentGameTarget? target = AddCurrentGame.From(
            _watcher.LastForeign,
            _watcher.LastForeignAt,
            DateTime.Now);

        if (target is null)
        {
            // The button should have been greyed out, so reaching here means the
            // memory went stale between the tab being drawn and the click landing -
            // twenty minutes of the window sitting open, most likely. Said plainly
            // rather than silently doing nothing.
            Flash("No game seen recently", true);
            RefreshAddPlayingState();
            return;
        }

        CurrentGameTarget game = target.Value;

        HotkeySlot? existing = AddCurrentGame.SlotFor(_settings.Slots, game.ExePath);

        if (existing is not null)
        {
            existing.AppExePath = game.ExePath;
            existing.AppName = game.Name;

            // Bound to the program that is running, so the executable is the
            // target. Any launcher link the slot used to carry belongs to the game
            // it used to name.
            existing.LaunchUri = string.Empty;
            existing.LaunchConfirmed = true;
            existing.IsSelfTarget = false;
            existing.IsAnyGameTarget = false;
            existing.ApplyOnStart = false;
            existing.AutoActivate = true;

            // The same claim rule every other way of setting a target goes through,
            // so a row that was already armed does not end up competing with another
            // slot for the same program.
            ReleaseAutoClaims(existing);

            Commit();
            BuildSlots();
            RegisterHotkeys();
            ApplyWatchState();

            Flash(existing.Name + " set to " + game.Name);
            RefreshAddPlayingState();
            return;
        }

        HotkeySlot slot = SlotService.NewSlot(_settings.Slots.Count + 1);
        slot.Name = game.Name;
        slot.AppExePath = game.ExePath;
        slot.AppName = game.Name;

        // Bound from the program running on this machine, so there is nothing to
        // confirm later: the user is looking at it.
        slot.LaunchConfirmed = true;

        // Armed, because the reason the user pressed this is that they are playing
        // it now and want it to load itself. Turning it on is the whole point of
        // binding the game that is running.
        slot.AutoActivate = true;

        _settings.Slots.Add(slot);
        ReleaseAutoClaims(slot);

        bool saved = Commit();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();

        if (saved)
        {
            Flash(game.Name + " added");
        }
        RefreshAddPlayingState();
    }


    /// <summary>
    /// Enables or greys out the "add what I am playing" control, and says why.
    /// </summary>
    /// <remarks>
    /// A control that does nothing when pressed is worse than one that is visibly
    /// unavailable, so the button follows the memory rather than the other way
    /// round: nothing remembered and it is off, which is the ordinary state on a
    /// machine where the user has only opened this window. The tooltip names the
    /// game it would bind, so the user can tell whether the answer is the one they
    /// want before committing to it.
    /// <para>
    /// Guarded on <see cref="_ready"/> because this is called from
    /// <see cref="SelectPage"/>, which the constructor reaches before every control
    /// on the page has been through its first pass.
    /// </para>
    /// </remarks>
    private void RefreshAddPlayingState()
    {
        if (!_ready)
        {
            return;
        }

        CurrentGameTarget? target = AddCurrentGame.From(
            _watcher.LastForeign,
            _watcher.LastForeignAt,
            DateTime.Now);

        AddPlayingButton.IsEnabled = target is not null;

        AddPlayingButton.ToolTip = target is { } game
            ? "Bind " + game.Name
            : "Play something first, then come back";

        System.Windows.Automation.AutomationProperties.SetName(
            AddPlayingButton,
            target is { } named
                ? "Add " + named.Name + " as a slot"
                : "No game seen recently");
    }


    /// <summary>
    /// Reorders a slot, and tells everything that reads the list in order.
    /// <para>
    /// The hotkeys are registered again rather than left alone: their ids are
    /// positions in this list, so a swap without a re-register would leave a key
    /// firing the slot that used to be beside it.
    /// </para>
    /// </summary>
    private void MoveSlot(HotkeySlot slot, int delta)
    {
        if (!SlotService.Move(_settings.Slots, slot, delta))
        {
            return;
        }

        bool saved = Commit();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();
        RefreshTraySlots();

        if (saved)
        {
            Flash(slot.Name + (delta < 0 ? " moved up" : " moved down"));
        }
    }


    /// <summary>
    /// Copies a slot and puts the copy directly after it, so the pair reads as
    /// related rather than the new one appearing at the bottom of a list the user
    /// is looking at the top of.
    /// </summary>
    private void DuplicateSlot(HotkeySlot source)
    {
        int at = _settings.Slots.FindIndex(x => x.Id == source.Id);
        if (at < 0)
        {
            return;
        }

        HotkeySlot copy = SlotService.Duplicate(source, _settings.Slots.Count + 1);
        _settings.Slots.Insert(at + 1, copy);

        bool saved = Commit();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();

        // No announce: the copy has no key and does nothing, so claiming it was
        // applied would be a lie, and the toast is the only thing saying so.
        if (saved)
        {
            Flash(copy.Name + " added");
        }
    }


    private static bool SameTarget(HotkeySlot a, HotkeySlot b)
    {
        if (a.IsSelfTarget || b.IsSelfTarget)
        {
            return a.IsSelfTarget && b.IsSelfTarget;
        }

        // Same shape as the check above it. Two wildcards claim the same set of
        // programs, so they are the same target and a duplicate of one another -
        // and without this the duplicate check would compare two empty paths and
        // call them different.
        if (a.IsAnyGameTarget || b.IsAnyGameTarget)
        {
            return a.IsAnyGameTarget && b.IsAnyGameTarget;
        }

        if (string.IsNullOrWhiteSpace(a.AppExePath) || string.IsNullOrWhiteSpace(b.AppExePath))
        {
            return false;
        }

        if (string.Equals(a.AppExePath, b.AppExePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            AppProfileTools.ProcessNameOf(a.AppExePath),
            AppProfileTools.ProcessNameOf(b.AppExePath),
            StringComparison.OrdinalIgnoreCase);
    }


    private string AutoToolTip(HotkeySlot slot)
    {
        if (!slot.HasWork)
        {
            return "Empty slot";
        }

        if (slot.IsSelfTarget)
        {
            return "On Startup";
        }

        // Said in full rather than as the row's own "ANY GAME" label, because this
        // is the tooltip where the qualifier belongs: the dropdown saying "any
        // game" does not say what stops it firing on every window, and a user who
        // does not know that will think it is broken.
        if (slot.IsAnyGameTarget)
        {
            return slot.AutoActivate
                ? "Auto: any fullscreen game not bound to another slot"
                : "Any fullscreen game, auto off";
        }

        if (!slot.HasTarget)
        {
            return "Pick a game first";
        }

        return "Auto: " + slot.TargetText;
    }


    private List<HotkeySlot> AutoClaimants(HotkeySlot slot)
    {
        return _settings.Slots
            .Where(s => s.Id != slot.Id && s.Enabled && s.AutoActivate && s.HasWork && s.HasTarget && SameTarget(s, slot))
            .ToList();
    }


    private void ReleaseAutoClaims(HotkeySlot slot)
    {
        foreach (HotkeySlot other in AutoClaimants(slot))
        {
            other.AutoActivate = false;
            Flash("Auto load off for " + other.Name);
        }

        // Deliberately no Commit or BuildSlots here. The callers that release a
        // claim are already in the middle of a change that commits and rebuilds
        // (a key capture, a game target, the auto switch), so doing it again
        // here rebuilt the whole board twice for one click.
        //
        // This used to exist twice, as ReleaseAutoClaims and ReleaseClaimsOn, with
        // byte-identical bodies and no behavioural difference between them. The
        // only thing distinguishing the two was the comment above, which is a good
        // way to find the pair and a bad reason to keep both: a reader could not
        // tell which one a given call site was supposed to use, and a change to
        // the claim rules had to be made twice or only in one of the two.
    }


    /// <summary>
    /// Names a slot the user added. The built in ones keep their names, because
    /// those names are how the slot is recognised on screen and in the log.
    /// </summary>
    private void RenameSlot(HotkeySlot slot)
    {
        if (slot.BuiltIn)
        {
            Flash("Built-in slots keep their name", true);
            return;
        }

        ShowModal("RENAME SLOT", slot.Name, text =>
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            slot.Name = trimmed;
            Commit();
            BuildSlots();
            RegisterHotkeys();
            Flash("Slot named " + trimmed);
        });
    }


    private void OnResetSlotsClick(object sender, RoutedEventArgs e)
    {
        ShowConfirmModal(
            "RESET ALL SLOTS?",
            "Every custom slot, game target and key you set is deleted and the six defaults come back. This cannot be undone.",
            "Reset slots",
            () =>
            {
                _settings.Slots = SlotService.DefaultSlots();
                Commit();
                BuildSlots();
                RegisterHotkeys();
                ApplyWatchState();
                Flash("Slots back to defaults");
            });
    }
}
