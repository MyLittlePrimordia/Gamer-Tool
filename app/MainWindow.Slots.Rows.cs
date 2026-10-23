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

// Slot board: the rows - the column widths, the caption strip, the card itself,
// and putting focus back where it was after a rebuild.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>
    /// The slot row's column widths, shared by a slot card and the caption strip
    /// above it so the two can never drift apart.
    /// <para>
    /// One row, nine controls, and it adds up. This was measured rather than
    /// guessed, twice: the first attempt assumed ten pixel gaps and generous
    /// minimums for the name and the game picker, came out 114px short, and the
    /// answer given was that a single row was not possible. It was possible.
    /// The name only has to hold a word rather than a field, and the game picker
    /// has the marquee, so 194px is a real width and not a compromise. The Play
    /// button paid for itself out of the gaps, which went from ten to six, so the
    /// game picker kept that width instead of losing a column's worth.
    /// </para>
    /// <para>
    /// What pays for the row is that four of these carry text the user wrote or
    /// a driver supplied - a preset name, a device name, a program name - and
    /// all four scroll their closed box. The open list sizes itself to its
    /// widest row, so nothing is ever actually cut off; what is given up is
    /// reading a long name without clicking it.
    /// </para>
    /// <para>
    /// The key is 100, which is the first time it has been moved. 84 was tried
    /// once and cut "CTRL+SHIFT+5" to "CTRL+SH"; 100 holds it in 10.5pt mono.
    /// </para>
    /// </summary>
    private static readonly GridLength[] SlotColumns =
    {
        new(120), new(6), new(100), new(6), new(142), new(6),
        new(142), new(6), new(116), new(6), new(100), new(6),
        new(130), new(6), new(1, GridUnitType.Star), new(6), new(24),
        new(6), new(30)
    };

    /// <summary>
    /// The keycap's height, and therefore the height of the whole row.
    /// <para>
    /// Named because the row is one line now, so the tallest thing in it is the
    /// keycap and everything else is centred against it. It used to be 28 as a
    /// literal on a grid that had a second row underneath it; there is no second
    /// row, and a literal here would be the only number in the card that nothing
    /// else could be measured against.
    /// </para>
    /// </summary>
    private const double KeyCapHeight = 28.0;

    private static void ApplySlotColumns(Grid row)
    {
        foreach (GridLength width in SlotColumns)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
    }


    // The overload taking explicit widths was dead: every call site uses the
    // parameterless one, because SlotColumns is the single definition of the row
    // and a caller passing a different set would produce a header that no longer
    // lined up with the row it labels. Removed rather than left, since the two
    // definitions of the same thing is exactly how they drifted apart.


    /// <summary>
    /// Captions for every control in a slot row. The strip is a child of the
    /// list's grid and carries the card's own 12px padding as a margin, so each
    /// caption lands exactly over the control it names.
    /// <para>
    /// The delete button has none. A caption over it would name what the icon
    /// beside it has always said, and a label that repeats a control is a line
    /// of the layout spent saying nothing.
    /// </para>
    /// </summary>
    private UIElement SlotHeaderRow()
    {
        Grid head = new();
        ApplySlotColumns(head);
        head.Margin = new Thickness(12, 0, 12, 8);

        foreach (TextBlock caption in Captions())
        {
            head.Children.Add(caption);
        }

        return head;
    }

    private const double CaptionHeight = 13.0;

    private IEnumerable<TextBlock> Captions()
    {
        (int Column, string Text)[] captions =
        {
            (0, "NAME"),
            (2, "HOTKEY"),
            (4, "DISPLAY"),
            (6, "SOUND"),
            (8, "CROSSHAIR"),
            (10, "MONITOR"),
            (12, "OUTPUT"),

            // "Autostart" rather than "Game", because that is what the column
            // decides. The dropdown is the only place auto-apply is set - picking
            // an app turns it on and "No game" turns it off - so the name that
            // describes what this control is for is the behaviour, not the
            // program. It is also the one that says what the column is for
            // without having to already know the app is what it is for.
            (14, "AUTOSTART"),
        };

        foreach ((int column, string text) in captions)
        {
            TextBlock caption = new()
            {
                Text = text,
                Style = (Style)FindResource("SectionHeader"),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(caption, column);
            yield return caption;
        }
    }


    /// <summary>
    /// Puts focus back on the same slot control after the rows were rebuilt.
    /// <para>
    /// Every control in a slot row carries a Tag of the slot id and which control
    /// it is, which is what makes this possible at all - the rows are torn down and
    /// reconstructed, so the element that had focus is gone and WPF drops focus to
    /// nothing. For a mouse user that is invisible. For someone changing a game's
    /// dropdown with the keyboard, unplugging a dock would throw them out of the
    /// slot board entirely, mid-edit, with no way back except tabbing from the top
    /// again.
    /// </para>
    /// <para>
    /// Only a row that is still there is restored, and only if it is still visible
    /// and enabled - a monitor dropdown that lost the screen it was pointed at may
    /// have been rebuilt as something else entirely.
    /// </para>
    /// </summary>
    private void RestoreSlotFocus(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        foreach (UIElement child in SlotList.Children)
        {
            if (FindByTag(child, tag!) is { } found
                && found.Focusable
                && found.IsVisible
                && found.IsEnabled)
            {
                found.Focus();
                return;
            }
        }
    }

    private static FrameworkElement? FindByTag(DependencyObject root, string tag)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is FrameworkElement { Tag: not null } element
                && string.Equals(element.Tag as string, tag, StringComparison.Ordinal))
            {
                return element;
            }

            if (FindByTag(child, tag) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>The tag of the slot control that currently has focus, if any.</summary>
    private string? FocusedSlotTag()
    {
        if (System.Windows.Input.Keyboard.FocusedElement is FrameworkElement { Tag: string tag } focused
            && SlotList.IsAncestorOf(focused))
        {
            return tag;
        }

        return null;
    }

    private void BuildSlots()
    {
        SlotList.Children.Clear();
        SlotList.ColumnDefinitions.Clear();
        SlotList.RowDefinitions.Clear();
        SlotList.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int count = _settings.Slots.Count;
        SlotCountText.Text = count == 0
            ? "No slots"
            : count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " slot" : " slots");

        // One line, under the list, and only when there is nothing to read. It
        // used to hold a permanent twenty two word instruction for a control that
        // already looks like a key and already explains itself on hover.
        SlotEmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SlotEmptyHint.Text = count == 0
            ? "Hit + to add a slot."
            : "Press a key box, then hold Ctrl or Alt and press the combo you want.";

        if (count == 0)
        {
            UpdateScreenHdrNote();
            return;
        }

        SlotList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SlotList.Children.Add(SlotHeaderRow());

        for (int i = 0; i < count; i++)
        {
            SlotList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UIElement card = SlotCard(_settings.Slots[i]);
            Grid.SetRow(card, i + 1);
            SlotList.Children.Add(card);
        }

        // The Display-tab note names a slot that asks for HDR without a screen.
        // Rebuilding the rows is exactly when that can have changed.
        UpdateScreenHdrNote();
    }


    /// <summary>
    /// Puts a slot's binding on its keycap, or the empty hint when it has none.
    /// Kept in one place so the saved binding and the caption can never drift
    /// apart, whichever direction the keycap is being reset from.
    /// </summary>
    private static void ShowSlotKey(TextBox keyBox, string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            keyBox.Text = "NOT SET";
            keyBox.ToolTip = "Click, then press the combo you want  ·  F1 to F24 bind on their own";
            return;
        }

        keyBox.Text = hotkey;
        keyBox.ToolTip = hotkey + "  ·  click to change, Esc cancels, Backspace clears";
    }


    /// <summary>
    /// Marks a keycap whose shortcut this process does not actually own.
    /// <para>
    /// Amber and an explanation in the tooltip, so the row keeps saying it after the
    /// toast that reported the refusal has gone. Without this the only evidence was
    /// one line of status text, and the user was left pressing a key that Windows had
    /// given to something else.
    /// </para>
    /// </summary>
    private void MarkDeadKey(TextBox keyBox, HotkeySlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.Hotkey) || !_deadHotkeySlots.Contains(slot.Id))
        {
            return;
        }

        keyBox.Foreground = (Brush)FindResource("Amber");
        keyBox.ToolTip = slot.Hotkey
            + "  ·  another program or another slot already owns this key, so it will not fire"
            + "  ·  click to change";
    }


    /// <summary>
    /// A single slot as two full width rows: the name, its key, and the two
    /// per slot actions above, and the four things it loads and when it fires
    /// below.
    /// <para>
    /// Two rows because one could not hold it. The output picker needed a
    /// hundred and fifty pixels that did not exist, and the only way to find them
    /// was to take them off controls whose whole job is to show a value - which
    /// had already left the game picker at a hundred and fifty, trying to hold an
    /// installed program's name. Rather than take the shortfall out of the
    /// pickers, the things that are not pickers moved up. The game picker gets
    /// nearly four hundred pixels and every other control is back to a width that
    /// fits what it is showing.
    /// </para>
    /// </summary>
    private UIElement SlotCard(HotkeySlot slot)
    {
        Border card = new()
        {
            Style = (Style)FindResource("Tile"),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        Grid row = new();
        ApplySlotColumns(row);

        card.Child = row;
        TextBox keyBox = new()
        {
            Style = (Style)FindResource("KeyCap"),
            Tag = slot.Id,
            Height = KeyCapHeight
        };

        ShowSlotKey(keyBox, slot.Hotkey);
        MarkDeadKey(keyBox, slot);

        System.Windows.Automation.AutomationProperties.SetName(
            keyBox, "Key for " + (string.IsNullOrWhiteSpace(slot.Name) ? "slot" : slot.Name));
        keyBox.GotKeyboardFocus += (s, e) =>
        {
            _captureSlotId = slot.Id;
            _captureBox = keyBox;

            // Listening state, spelled the way every launcher spells it. The
            // keycap is read only, so this is the only way it ever says anything
            // other than the current binding.
            keyBox.Text = "PRESS A KEY";
            keyBox.ToolTip = "Esc cancels  ·  Backspace clears  ·  F1 to F24 bind on their own";
        };
        keyBox.LostKeyboardFocus += (s, e) =>
        {
            if (!ReferenceEquals(_captureBox, keyBox))
            {
                return;
            }

            // Clicked away without choosing anything, so the slot keeps what it
            // had. Without this the "PRESS A KEY" prompt or the modifier hint
            // stays on screen pretending to be a binding.
            ShowSlotKey(keyBox, slot.Hotkey);
            MarkDeadKey(keyBox, slot);
            _captureSlotId = null;
            _captureBox = null;
        };
        Grid.SetColumn(keyBox, 2);

        // No font, no size and no weight here. They are on the NamePlate style,
        // beside the border and the padding, so the five values that make this
        // look like the dropdown next to it are all in one place. Setting any of
        // them here would put one of them in two places and one of them would
        // eventually be the odd one out.
        TextBlock nameText = new()
        {
            Text = string.IsNullOrWhiteSpace(slot.Name) ? "Slot" : slot.Name,

            // The plate is 140px and the longest shipped name is about 105 of
            // them, so a name the user wrote has room to be trimmed rather than
            // pushing the plate wider. A slot name is an identifier more than a
            // label, and the full string is in the rename box it opens.
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // The name is the whole of what this row does, so it is given a plate
        // rather than being bare text in a column nine hundred pixels wide.
        // A bare TextBlock with a transparent background is a click target the
        // width of the word with nothing drawn on it, and the only clue it was
        // live was the pointer changing - which is a clue you have to already
        // know to look for. The plate is the clue.
        //
        // A Button, so the plate can carry the box and the hover, and so
        // renaming a slot no longer needs a mouse: a bare TextBlock that only
        // answered a click was not reachable from the keyboard at all.
        //
        // Built in slots are disabled rather than styled differently, so the row
        // reads the same as its neighbours and the dimmed plate is the thing
        // saying "this one is fixed". A name that looked editable and then
        // refused would be worse than one that never looked editable.
        Button namePlate = new()
        {
            Style = (Style)FindResource("NamePlate"),
            Content = nameText,
            IsEnabled = !slot.BuiltIn,
            Tag = slot.Id,
            ToolTip = slot.BuiltIn
                ? (slot.HasWork ? slot.WorkText : "Empty slot")
                : "Rename"
        };

        if (!slot.BuiltIn)
        {
            namePlate.Click += (s, e) => RenameSlot(slot);
            System.Windows.Automation.AutomationProperties.SetName(namePlate, "Rename " + slot.Name);
        }

        Grid.SetColumn(namePlate, 0);

        ComboBox displayBox = PresetCombo("display", slot);
        Grid.SetColumn(displayBox, 4);

        ComboBox soundBox = PresetCombo("audio", slot);
        Grid.SetColumn(soundBox, 6);

        ComboBox crosshairBox = CrosshairCombo(slot);
        Grid.SetColumn(crosshairBox, 8);

        ComboBox monitorBox = MonitorCombo(slot);
        Grid.SetColumn(monitorBox, 10);

        ComboBox outputBox = OutputCombo(slot);
        Grid.SetColumn(outputBox, 12);

        // The game picker is on this row, not the other one. It is what fills
        // the space the name was floating in, and it belongs beside the key it
        // is the hotkey for rather than beside a monitor and an EQ curve it has
        // nothing to do with. It is also the widest string on the card by a long
        // way - an installed program's name - so it is the column that most
        // needed the room.
        ComboBox appBox = AppCombo(slot);
        Grid.SetColumn(appBox, 14);

        // Play is the one control here that does something outside the window:
        // it applies the slot before starting the game, so the profile is already
        // on the screen when the game gives it up. The tooltip is the pre-flight
        // checklist, built from the same object the click uses, so what the user
        // reads on hover is what the app has actually decided.
        IconButton play = new()
        {
            Style = (Style)FindResource("IconButton"),
            Icon = Icons.Play,
            Tag = slot.Id,
            Width = 24,
            Height = 28,
            ToolTip = PlayPreflightFor(slot).Tooltip,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetShowOnDisabled(play, true);
        System.Windows.Automation.AutomationProperties.SetName(
            play, "Play " + slot.Name + " with its profile");
        play.Click += OnSlotPlayClick;
        Grid.SetColumn(play, 16);

        IconButton remove = new()
        {
            Style = (Style)FindResource("IconButton"),
            Icon = Icons.Trash,
            Tag = slot.Id,
            ToolTip = "Delete Slot",

            // The style is 30, two more than anything else on the row, and it was
            // what decided the row's height: the keycap's 28 was the height of four
            // controls, not of the card. Pinned to the keycap here so the row is 28
            // and the comment on KeyCapHeight is true of the card.
            Height = KeyCapHeight,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        // IconButton's template has no ContentPresenter - it draws a Path from a
        // Geometry property - so nothing in the visual tree can supply a name and
        // the tooltip arrives as HelpText rather than as one. Every delete button
        // in a list of slots has to say which slot it deletes.
        System.Windows.Automation.AutomationProperties.SetName(remove, "Delete slot " + slot.Name);
        remove.Click += (s, e) =>
        {
            _settings.Slots.RemoveAll(x => x.Id == slot.Id);
            Commit();
            BuildSlots();
            RegisterHotkeys();
            ApplyWatchState();
        };
        Grid.SetColumn(remove, 18);

        row.Children.Add(namePlate);
        row.Children.Add(keyBox);
        row.Children.Add(displayBox);
        row.Children.Add(soundBox);
        row.Children.Add(crosshairBox);
        row.Children.Add(monitorBox);
        row.Children.Add(outputBox);
        row.Children.Add(appBox);
        row.Children.Add(play);
        row.Children.Add(remove);

        // Duplicate and the shortcut live here rather than as buttons.
        //
        // Duplicate is a convenience rather than something done often enough to
        // earn a permanent place. The shortcut is the same action as Play, just in
        // a form that can sit on the desktop; the row already has one button for
        // the action itself. Both are context menu items: no width at all, and
        // nothing to line up.
        ContextMenu menu = new();

        // HDR is per slot and per screen, so it belongs with the thing that owns
        // both. The row has no room for another column, and the dialog is the
        // same shape as the audio routing one.
        MenuItem hdr = new() { Header = "HDR on this screen..." };
        hdr.Click += (s, e) => ShowHdrDialog(slot);
        menu.Items.Add(hdr);

        // The arguments a game is started with are the same kind of thing: per slot,
        // rarely touched, and worth a field rather than a column of its own.
        MenuItem launch = new() { Header = "Launch options..." };
        launch.Click += (s, e) => ShowLaunchOptionsDialog(slot);
        menu.Items.Add(launch);
        menu.Items.Add(new Separator());

        MenuItem route = new() { Header = "Windows audio routing..." };
        route.Click += (s, e) => ShowWindowsAudioRoutingDialog(slot);
        menu.Items.Add(route);
        menu.Items.Add(new Separator());
        MenuItem duplicate = new() { Header = "Duplicate" };
        duplicate.Click += (s, e) => DuplicateSlot(slot);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());
        MenuItem shortcut = new() { Header = "Create Play shortcut" };
        shortcut.Click += (s, e) => CreatePlayShortcut(slot);
        menu.Items.Add(shortcut);

        // Order, because the tray list and the cycle key read this list top to
        // bottom. Disabled at the ends rather than allowed and ignored, so the menu
        // says which direction is available instead of doing nothing on a click.
        int position = _settings.Slots.IndexOf(slot);

        menu.Items.Add(new Separator());

        MenuItem up = new() { Header = "Move up", IsEnabled = position > 0 };
        up.Click += (s, e) => MoveSlot(slot, -1);
        menu.Items.Add(up);

        MenuItem down = new()
        {
            Header = "Move down",
            IsEnabled = position >= 0 && position < _settings.Slots.Count - 1,
        };
        down.Click += (s, e) => MoveSlot(slot, 1);
        menu.Items.Add(down);

        card.ContextMenu = menu;

        return card;
    }
}
