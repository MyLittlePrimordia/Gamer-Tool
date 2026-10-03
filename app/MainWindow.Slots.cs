using System;
using System.Collections.Generic;
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
    /// One slot per full width row with everything on a single line, so the
    /// dropdowns can be wide enough to show a real preset name and a real game
    /// name instead of truncating to nothing. The game target takes the leftover
    /// width because game names are the longest of the three. This is the only
    /// page that scrolls, and it scrolls on a slim dark bar.
    /// </summary>
    /// <summary>
    /// The slot row's column widths, shared by a slot card and the caption strip
    /// above it so the two can never drift apart.
    /// <para>
    /// One row, eight controls, and it adds up. This was measured rather than
    /// guessed, twice: the first attempt assumed ten pixel gaps and generous
    /// minimums for the name and the game picker, came out 114px short, and the
    /// answer given was that a single row was not possible. It was possible.
    /// The name only has to hold a word rather than a field, and the game picker
    /// has the marquee, so 194px is a real width and not a compromise.
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
        new(140), new(10), new(100), new(10), new(160), new(10),
        new(160), new(10), new(118), new(10), new(180), new(10),
        new(1, GridUnitType.Star), new(10), new(30)
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
            (8, "MONITOR"),
            (10, "OUTPUT"),

            // "Autostart" rather than "Game", because that is what the column
            // decides. The dropdown is the only place auto-apply is set - picking
            // an app turns it on and "No game" turns it off - so the name that
            // describes what this control is for is the behaviour, not the
            // program. It is also the one that says what the column is for
            // without having to already know the app is what it is for.
            (12, "AUTOSTART"),
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
            return;

        SlotList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SlotList.Children.Add(SlotHeaderRow());

        for (int i = 0; i < count; i++)
        {
            SlotList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UIElement card = SlotCard(_settings.Slots[i]);
            Grid.SetRow(card, i + 1);
            SlotList.Children.Add(card);
        }
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

        ComboBox monitorBox = MonitorCombo(slot);
        Grid.SetColumn(monitorBox, 8);

        ComboBox outputBox = OutputCombo(slot);
        Grid.SetColumn(outputBox, 10);

        // The game picker is on this row, not the other one. It is what fills
        // the space the name was floating in, and it belongs beside the key it
        // is the hotkey for rather than beside a monitor and an EQ curve it has
        // nothing to do with. It is also the widest string on the card by a long
        // way - an installed program's name - so it is the column that most
        // needed the room.
        ComboBox appBox = AppCombo(slot);
        Grid.SetColumn(appBox, 12);

        IconButton remove = new()
        {
            Style = (Style)FindResource("IconButton"),
            Icon = Icons.Trash,
            Tag = slot.Id,
            ToolTip = "Delete Slot",
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
        Grid.SetColumn(remove, 14);

        row.Children.Add(namePlate);
        row.Children.Add(keyBox);
        row.Children.Add(displayBox);
        row.Children.Add(soundBox);
        row.Children.Add(monitorBox);
        row.Children.Add(outputBox);
        row.Children.Add(appBox);
        row.Children.Add(remove);

        // Duplicate lives here rather than as a third button.
        //
        // It is the one action in here that is a convenience rather than
        // something done often enough to earn a permanent place, and the name row
        // has a gap at the right of the name that would look like a missing
        // control if something were left out of it. So it is a context menu: no
        // width at all, and nothing to line up.
        ContextMenu menu = new();
        MenuItem duplicate = new() { Header = "Duplicate" };
        duplicate.Click += (s, e) => DuplicateSlot(slot);
        menu.Items.Add(duplicate);
        card.ContextMenu = menu;

        return card;
    }


    /// <summary>
    /// Where this slot's sound goes, in the row rather than in a menu.
    /// <para>
    /// This started as an item on the card's context menu, which was the obvious
    /// place given the row has no room for another column. It was wrong for a
    /// reason that only showed up on a real machine: the cards are built when the
    /// tab opens, and the device list arrives afterwards, so the menu was baked
    /// with whatever devices existed at that moment and could be empty. A
    /// control that shows the current value without being asked to be reopened
    /// is not a menu item, it is a column.
    /// </para>
    /// <para>
    /// "System default" is the empty value and is what every slot starts on, and
    /// it is the same entry the Settings dropdown shows, by name and by
    /// constant. The user has to be able to tell that the row's default is the
    /// setting it follows, and "Follow the app" did not say that - it described
    /// the mechanism rather than naming the choice, so the two pickers looked
    /// like two different things.
    /// </para>
    /// <para>
    /// The marquee is the same treatment the display preset column gets, and for
    /// the same reason. Output device names come off drivers and are the longest
    /// strings on this row - "Headset (HyperX Cloud II USB Audio)" is not a rare
    /// one - and the closed box has a fixed two hundred pixels to show them in.
    /// The open list sizes itself to its widest row, so nothing is cut off once
    /// it is open.
    /// </para>
    /// </summary>
    private ComboBox OutputCombo(HotkeySlot slot)
    {
        List<DeviceChoice> choices = new(_knownOutputDevices.Count + 1)
        {
            new DeviceChoice { Id = string.Empty, Name = DeviceChoice.SystemDefaultName }
        };

        foreach (string device in _knownOutputDevices)
        {
            choices.Add(new DeviceChoice { Id = device, Name = device });
        }

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|output",
            ToolTip = "Which output this slot plays through"
        };
        System.Windows.Automation.AutomationProperties.SetName(
            box, "Output for " + slot.Name);

        MarqueeBox.SetAllowMarquee(box, true);

        int index = choices.FindIndex(c =>
            string.Equals(c.Id, slot.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = index < 0 ? 0 : index;

        box.SelectionChanged += OnSlotOutputChanged;
        return box;
    }


    private void OnSlotOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not ComboBox box || box.Tag is not string tag || box.SelectedItem is not DeviceChoice choice)
        {
            return;
        }

        string id = tag.Split('|')[0];
        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == id);
        if (slot is null)
        {
            return;
        }

        slot.OutputDeviceId = choice.Id;
        Commit();

        // Said here rather than left to the next apply, because the choice is
        // about a route and the user needs to know a route changed rather than
        // finding out when a tune lands somewhere else. Only the non default
        // case is worth a line: every slot following the app is the ordinary
        // state and saying so sixty times would be noise.
        if (!string.IsNullOrWhiteSpace(choice.Id))
        {
            Flash(slot.Name + " plays through " + choice.Id);
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

        Commit();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();

        // No announce: the copy has no key and does nothing, so claiming it was
        // applied would be a lie, and the toast is the only thing saying so.
        Flash(copy.Name + " added");
    }


    private ComboBox MonitorCombo(HotkeySlot slot)
    {
        IReadOnlyList<MonitorChoice> choices = _display.MonitorChoices;
        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|monitor",
            ToolTip = "Screens"
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Screen for " + slot.Name);

        // A monitor name is whatever EDID says the panel is, which for a
        // well-specified display is a model number longer than 118 pixels. The
        // closed box scrolls rather than clipping, and the open list is sized to
        // its widest row, so nothing is actually lost.
        MarqueeBox.SetAllowMarquee(box, true);

        int index = 0;
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Device, slot.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        box.SelectedIndex = index;
        box.SelectionChanged += OnSlotMonitorChanged;
        return box;
    }


    private void OnSlotMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not ComboBox box || box.Tag is not string tag || box.SelectedItem is not MonitorChoice choice)
        {
            return;
        }

        string[] parts = tag.Split('|');
        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == parts[0]);
        if (slot is null)
        {
            return;
        }

        slot.MonitorDevice = choice.Device;
        Commit();
    }


    private ComboBox PresetCombo(string kind, HotkeySlot slot)
    {
        List<PresetChoice> choices = new() { new PresetChoice { Kind = kind, Id = string.Empty, Name = "No screen" } };

        if (kind == "display")
        {
            foreach (DisplayPreset preset in AllDisplayPresets())
            {
                choices.Add(new PresetChoice { Kind = kind, Id = preset.Id, Name = preset.Name });
            }
        }
        else
        {
            foreach (AudioPreset preset in AllAudioPresets())
            {
                choices.Add(new PresetChoice { Kind = kind, Id = preset.Id, Name = preset.Name });
            }

            choices[0].Name = "No sound";
        }

        // Named per slot rather than once for the whole list. A screen reader walking
        // this column otherwise announces a row of identical unnamed drop-downs
        // with nothing to say which slot any of them belongs to, and the caption
        // strip above is not a label for a control - it is a TextBlock. The slot
        // name is the only thing on the row that says which row it is.
        string name = kind == "display" ? "Screen Preset" : "Sound Preset";

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|" + kind,
            ToolTip = name
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name + " for " + slot.Name);

        // Preset names are the user's to write, so a long one has to stay
        // readable in a fixed width column. Only the closed box needs this: the
        // open list sizes itself to its widest row, so nothing is cut off there.
        // The sound picker is left out, its names are all short and set by us.
        if (kind == "display")
            MarqueeBox.SetAllowMarquee(box, true);

        int index = choices.FindIndex(c => string.Equals(c.Id, kind == "display" ? slot.DisplayPresetId : slot.AudioPresetId, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = index < 0 ? 0 : index;
        box.SelectionChanged += OnSlotPresetChanged;
        return box;
    }


    /// <summary>
    /// Pulls launcher icons for the real app targets. Only files that exist are
    /// touched, and IconFactory caches per path so each app is read once.
    /// </summary>
    /// <summary>
    /// Fills in the icon on any row that does not have one yet.
    /// <para>
    /// Cache first, then read from disk if it is not there. That order is the fix,
    /// and the second half is not optional.
    /// </para>
    /// <para>
    /// A version of this only looked in the cache, on the theory that a background
    /// pass had already filled it. Measured against a cold cache: no row got an icon,
    /// and every one of them had one available. Anything that opened a picker
    /// before that background pass finished saw an empty list, and there was no
    /// recovery, because the picker had no way of getting an icon on its own. The
    /// running-games entries were the worst of it - they are added as the dropdown
    /// opens and were never in the pass at all, so the games actually being played
    /// were permanently blank.
    /// </para>
    /// <para>
    /// Reading here is what the original code did and it worked, so it stays as the
    /// floor: the picker can always get its own icons. The background pass is kept
    /// because it is what stops that floor from costing anything - when it has
    /// finished, which is the usual case by the time a user opens a picker, every
    /// row below is a dictionary hit.
    /// </para>
    /// </summary>
    private static void ResolveAppIcons(List<AppCandidate> candidates)
    {
        foreach (AppCandidate candidate in candidates)
        {
            if (candidate.Icon is not null || candidate.ExePath.Length == 0)
            {
                continue;
            }

            // The three modes have no file behind them, so there is nothing to
            // pull an icon out of. Without this each would try to read its own
            // marker string as a path.
            if (candidate.ExePath == SelfMarker
                || candidate.ExePath == BrowseMarker
                || candidate.ExePath == AnyGameMarker)
            {
                continue;
            }

            if (IconFactory.TryGetCachedAppIcon(candidate.ExePath, IconFactory.WarmSize, out var icon))
            {
                candidate.Icon = icon;
                continue;
            }

            // Not warmed yet. Read it now rather than leaving the row blank: this is
            // the original behaviour, it is slower, and it is the reason the picker
            // shows icons at all rather than depending on a race it does not control.
            candidate.Icon = IconFactory.ExtractAppIcon(candidate.ExePath, IconFactory.WarmSize);
        }
    }


    /// <summary>
    /// Fills in icons on a background thread and reports back to the dispatcher.
    /// <para>
    /// For rows that arrive after the scan, which the warm pass cannot have seen.
    /// There are only ever a handful - the programs running right now - so unlike
    /// the library-wide pass this is not competing with anything, and it is worth
    /// moving off the dispatcher anyway so a slow icon cannot hold the list the user
    /// is looking at.
    /// </para>
    /// </summary>
    private static async System.Threading.Tasks.Task ResolveIconsOffThreadAsync(IReadOnlyList<AppCandidate> candidates)
    {
        foreach (AppCandidate candidate in candidates)
        {
            if (candidate.Icon is not null || candidate.ExePath.Length == 0)
            {
                continue;
            }

            string path = candidate.ExePath;
            System.Windows.Media.Imaging.BitmapSource? icon =
                await System.Threading.Tasks.Task.Run(
                    () => IconFactory.ExtractAppIcon(path, IconFactory.WarmSize));

            if (icon is null)
            {
                continue;
            }

            // Back on the dispatcher, which is where the setter's change
            // notification has to be raised from.
            candidate.Icon = icon;
            await System.Threading.Tasks.Task.Yield();
        }
    }


    /// <summary>
    /// Reads every icon in the library into the cache, on a background thread.
    /// <para>
    /// Fired once a scan has settled and never awaited. Every slot's dropdown is
    /// built from the same <see cref="AppCandidate"/> objects this list holds rather
    /// than copies of them, so one pass here is enough for every picker in the app -
    /// and because it only fills a dictionary, it does not matter that the lists are
    /// already bound and that nothing is told about it.
    /// </para>
    /// <para>
    /// About a second of disk reads for a large library, on a thread that is not the
    /// one drawing anything.
    /// </para>
    /// </summary>
    private void WarmAppIcons()
    {
        List<AppCandidate> warming = _appList;

        _ = System.Threading.Tasks.Task.Run(() =>
            IconFactory.WarmAppIcons(
                warming
                    .Where(c => c.ExePath.Length > 0
                        && c.ExePath != SelfMarker
                        && c.ExePath != BrowseMarker
                        && c.ExePath != AnyGameMarker)
                    .Select(c => c.ExePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)));
    }


    /// <summary>
    /// Puts the currently running programs into an open dropdown, above the
    /// installed ones.
    /// <para>
    /// Binding a slot to a game is only as good as the path it was bound to, and
    /// scanning the registry and the Steam library is a guess at it. This asks the
    /// machine instead, which is how a game installed somewhere unusual, or behind
    /// a launcher, is found at all.
    /// </para>
    /// <para>
    /// Above the installed list because a game the user can see running is the one
    /// they are trying to bind, and it is the entry they would otherwise have to
    /// recognise in a list of several hundred names. Before the "Browse" line and
    /// after the two modes, so the row order the selection code depends on - modes
    /// first, games from index 3 - is unchanged.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task AddRunningAppsAsync(
        ComboBox box, List<AppCandidate> choices, HotkeySlot slot)
    {
        // Guarded rather than done once. DropDownOpened fires every time the list is
        // opened, and without this the list would grow by one copy of every running
        // program each time.
        choices.RemoveAll(c => c.Source == "RUNNING");

        if (_quitting)
        {
            return;
        }

        IReadOnlyList<AppCandidate> running;
        try
        {
            // Off the dispatcher. This walks every process on the machine and reads
            // an image path for each one with a window, which is enough to be felt
            // as a hitch if it ran on the thread the dropdown is drawing on.
            running = await System.Threading.Tasks.Task.Run(() => _library.ScanRunning());
        }
        catch (Exception ex)
        {
            TraceLog.Write("RUNNING", ex);
            return;
        }

        if (_quitting || running.Count == 0)
        {
            return;
        }

        // Whatever the dropdown is currently showing is preserved, because it is
        // rebuilt underneath the open list. Without this, opening the dropdown
        // cleared a selection the user had already made.
        object? wasSelected = box.SelectedItem;
        int wasIndex = box.SelectedIndex;

        int insertAt = choices.Count(c => c.Source is "NONE" or "SELF" or "ANY");
        choices.InsertRange(insertAt, running);

        // Icons for these, off the dispatcher, before they go on screen.
        //
        // They are the rows a user is most likely to want - the game they have
        // open right now - and they are the one set the scan-time warm pass never
        // sees, because they do not exist until this moment. They were left to the
        // dropdown's own resolve, which for a while could only read a cache it had
        // no reason to be warm for, so the games actually being played came up
        // blank while the installed ones did not.
        _ = ResolveIconsOffThreadAsync(running);

        box.Items.Refresh();

        // Re-found by path rather than by index, because inserting the running
        // programs shifts every index below the insertion point - and the code that
        // reads the selection by index would then pick the wrong game.
        if (wasSelected is AppCandidate previous)
        {
            foreach (object item in box.Items)
            {
                if (item is AppCandidate candidate
                    && candidate.Source == previous.Source
                    && string.Equals(candidate.ExePath, previous.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = candidate;
                    break;
                }
            }
        }
        else if (wasIndex >= 0)
        {
            box.SelectedIndex = wasIndex < insertAt ? wasIndex : wasIndex + running.Count;
        }
    }


    private ComboBox AppCombo(HotkeySlot slot)
    {
        List<AppCandidate> choices = new()
        {
            new AppCandidate { Name = "No game", ExePath = string.Empty, ProcessName = string.Empty, Source = "NONE" },
            new AppCandidate { Name = "Gamer Tool (on start)", ExePath = SelfMarker, ProcessName = string.Empty, Source = "SELF", Icon = IconFactory.LoadWindowIcon() },

            // The wildcard, beside the two other modes rather than at the bottom of
            // the list with several hundred installed games. It is a mode, not a
            // target, and buried under a Steam library it would never be found.
            new AppCandidate { Name = "Any game (fullscreen)", ExePath = AnyGameMarker, ProcessName = string.Empty, Source = "ANY" }
        };
        choices.AddRange(_appList.Where(c => !string.IsNullOrWhiteSpace(c.ExePath)));
        choices.Add(new AppCandidate { Name = "Browse for a file...", ExePath = BrowseMarker, ProcessName = string.Empty, Source = "BROWSE" });

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemTemplate = (DataTemplate)FindResource("AppIconItem"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ItemsSource = choices,
            Tag = slot.Id + "|app",

            // The auto-apply explanation lived on a switch that is gone. It is
            // the game dropdown that decides whether a slot loads itself, so it
            // is also the only place the answer can be read from.
            ToolTip = AutoToolTip(slot)
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Game for " + slot.Name);

        // Installed programs have long names and this column is narrow, so the
        // closed box scrolls rather than clipping.
        MarqueeBox.SetAllowMarquee(box, true);

        // Icons are pulled the first time the list is actually opened, not on startup,
        // and off the dispatcher so the drop is not held up by it. By this point
        // the scan has usually already warmed them, so there is nothing to do and
        // the list opens complete.
        box.DropDownOpened += (_, _) => ResolveAppIcons(choices);

        // The running games are put in as the dropdown opens, not baked into the
        // list when the slot row was built. What is running changes minute to
        // minute, and a list that showed the programs open at the time the Hotkeys
        // tab was last built would be the wrong answer by the time the user looked
        // for their game.
        box.DropDownOpened += (_, _) => _ = AddRunningAppsAsync(box, choices, slot);

        // Index 2 is the wildcard, so the installed games start at 3. Hard-coded
        // rather than counted, because a count would silently shift every index
        // below it whenever a mode were added - and the symptom would be a game
        // row that selects the wrong preset.
        int index = slot.IsSelfTarget ? 1 : slot.IsAnyGameTarget ? 2 : 0;
        if (!slot.IsSelfTarget && !slot.IsAnyGameTarget && !string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            index = -1;
            for (int i = 3; i < choices.Count; i++)
            {
                if (string.Equals(choices[i].ExePath, slot.AppExePath, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                AppCandidate manual = new()
                {
                    Name = string.IsNullOrWhiteSpace(slot.AppName) ? "Picked" : slot.AppName,
                    ExePath = slot.AppExePath,
                    ProcessName = AppProfileTools.ProcessNameOf(slot.AppExePath),
                    Source = "MANUAL"
                };
                manual.Icon = IconFactory.ExtractAppIcon(manual.ExePath);
                choices.Insert(3, manual);
                box.ItemsSource = choices;
                index = 3;
            }
        }

        box.SelectedIndex = index;
        box.SelectionChanged += OnSlotAppChanged;
        return box;
    }


    private const string BrowseMarker = "\\browse";


    private const string SelfMarker = "\\self";


    /// <summary>
    /// The wildcard entry in a slot's game dropdown: not bound to any program.
    /// <para>
    /// A marker rather than a path, so it cannot collide with a real file - the
    /// same reason <see cref="SelfMarker"/> exists. Selected here rather than typed,
    /// because it is a mode rather than a target and reading it off the dropdown
    /// says which.
    /// </para>
    /// </summary>
    private const string AnyGameMarker = "\\any";


    /// <summary>
    /// Identifies the panic key inside the hotkey service. Slot bindings are
    /// "slot:" plus the slot id, so this has to be a shape no slot can produce.
    /// </summary>
    private const string EmergencyTargetId = "emergency";


    /// <summary>
    /// Registration id for the panic key. Slot ids count up from one and there
    /// are a handful of slots, so a high number keeps the two apart without
    /// threading a counter through.
    /// </summary>
    private const int EmergencyHotkeyId = 1000;


    /// <summary>
    /// Identifies the bypass key inside the hotkey service. Same shape as
    /// <see cref="EmergencyTargetId"/> and for the same reason: "slot:" plus a slot
    /// id is the only other thing the service is ever asked about, so any other
    /// string is safe.
    /// </summary>
    private const string BypassTargetId = "bypass";


    /// <summary>
    /// Registration id for the bypass key. One above the panic key's, which is
    /// arbitrary but leaves room if a third non-slot binding ever appears.
    /// </summary>
    private const int BypassHotkeyId = 1001;


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


    private void OnSlotPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not ComboBox box || box.Tag is not string tag || box.SelectedItem is not PresetChoice choice)
        {
            return;
        }

        string[] parts = tag.Split('|');
        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == parts[0]);
        if (slot is null)
        {
            return;
        }

        if (parts[1] == "display")
        {
            slot.DisplayPresetId = choice.Id.Length == 0 ? null : choice.Id;
        }
        else
        {
            slot.AudioPresetId = choice.Id.Length == 0 ? null : choice.Id;
        }

        if (!slot.HasWork)
        {
            slot.AutoActivate = false;
            Flash("Slot empty, auto load off", true);
        }

        Commit();
        BuildSlots();
    }


    private void OnSlotAppChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not ComboBox box || box.Tag is not string tag || box.SelectedItem is not AppCandidate choice)
        {
            return;
        }

        string[] parts = tag.Split('|');
        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == parts[0]);
        if (slot is null)
        {
            return;
        }

        if (string.Equals(choice.ExePath, SelfMarker, StringComparison.Ordinal))
        {
            slot.IsSelfTarget = true;
            slot.ApplyOnStart = true;
            slot.AutoActivate = false;
            slot.AppExePath = null;
            slot.AppName = null;

            // The three modes are mutually exclusive. Choosing one has to clear
            // the other two, because HasTarget, TargetText and the matching code
            // all read them as independent flags - and a slot that is both "on
            // start" and "any game" would arm two different mechanisms and revert
            // through whichever happened to be checked first.
            slot.IsAnyGameTarget = false;

            ReleaseAutoClaims(slot);
            Commit();
            BuildSlots();
            ApplyWatchState();
            return;
        }

        if (string.Equals(choice.ExePath, AnyGameMarker, StringComparison.Ordinal))
        {
            slot.IsAnyGameTarget = true;
            slot.IsSelfTarget = false;
            slot.ApplyOnStart = false;
            slot.AppExePath = null;
            slot.AppName = null;

            // Armed, because a wildcard that has to be switched on separately is
            // the same discoverability problem it is meant to solve. At most one
            // ends up armed; ResolveAutoClaims stands the rest down and says so
            // through the toast below rather than silently.
            slot.AutoActivate = true;

            ReleaseAutoClaims(slot);
            bool kept = slot.AutoActivate;

            Commit();
            BuildSlots();
            ApplyWatchState();

            if (!kept)
            {
                Flash("Another slot already claims any game", true);
            }

            return;
        }

        if (string.Equals(choice.ExePath, BrowseMarker, StringComparison.Ordinal))
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "PICK THE GAME OR APP",
                Filter = "Programs|*.exe;*.bat;*.lnk;*.cmd|All files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                string picked = ResolvePickedPath(dialog.FileName);
                slot.AppExePath = picked;
                slot.AppName = System.IO.Path.GetFileNameWithoutExtension(picked);

                // A picked file is a specific target, so it clears the wildcard
                // rather than sitting alongside it. Same reason as the two above.
                slot.IsAnyGameTarget = false;

                slot.AutoActivate = true;
                ReleaseAutoClaims(slot);
                Flash("Target set to " + slot.AppName);
            }

            Commit();
            BuildSlots();
            ApplyWatchState();
            return;
        }

        slot.IsSelfTarget = false;
        slot.IsAnyGameTarget = false;
        slot.ApplyOnStart = false;
        slot.AppExePath = choice.ExePath.Length == 0 ? null : choice.ExePath;
        slot.AppName = choice.ExePath.Length == 0 ? null : choice.Name;
        if (slot.AppExePath is not null)
        {
            slot.AutoActivate = true;
            ReleaseAutoClaims(slot);
        }
        else
        {
            // Cleared, because the alternative is a state the switch itself
            // refuses to create: choosing "No game" left AutoActivate at true, so
            // the row then showed AUTO on and enabled for a slot with nothing to
            // auto-load on, and it was written to the profile that way. The same
            // switch, touched by hand in that state, flashes "Pick a game in the
            // last dropdown", so the two halves of the app disagreed.
            //
            // The claim goes with it. Leaving a slot holding an automatic claim on
            // a game it no longer points at is how two slots end up competing for
            // one target, which is what the claim resolution exists to prevent.
            slot.AutoActivate = false;
            ReleaseAutoClaims(slot);
        }

        Commit();
        BuildSlots();
        ApplyWatchState();
    }


    private static string ResolvePickedPath(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return path;
        }

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return path;
            }

            object? shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                return path;
            }

            object? link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shellObject, new object[] { path });
            if (link is null)
            {
                return path;
            }

            object? raw = link.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null);
            string? target = raw as string;
            return string.IsNullOrWhiteSpace(target) ? path : target;
        }
        catch (Exception ex)
        {
            TraceLog.Write("SHORTCUT", ex);
            return path;
        }
    }


    /// <summary>1 while a scan is running, so two of them never overlap.</summary>
    private int _scanBusy;

    /// <param name="force">
    /// Rescan even when the list already has something in it.
    /// <para>
    /// The scan button was calling the non-forcing version, so after the Hotkeys
    /// tab had warmed the list once - which it does every time it is opened - the
    /// button did nothing at all. It still said "SCANNING FOR GAMES" and still
    /// reported a count, so it looked like a scan that had found nothing new,
    /// which is exactly what someone who has just installed a game concludes.
    /// </para>
    /// </param>
    /// <summary>
    /// Builds the app list, unless one is already being built.
    /// <para>
    /// There was no guard here, and this is reached from four places: the scan
    /// button, the first time a slot's game dropdown opens, a restore, and a
    /// window state change. The scan walks every Steam library and every
    /// uninstall subkey on the machine, so a second click on the button - which
    /// force:true permits, since it deliberately bypasses the cache - ran it
    /// twice at once, and whichever finished last overwrote the other's result
    /// with a list that may have been built mid-scan.
    /// </para>
    /// <para>
    /// The pattern is the same one the FxSound state refresh uses three files
    /// away: take an interlocked flag, and re-arm in the finally for a request
    /// that arrived while the work was in flight.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task EnsureAppList(bool force = false)
    {
        if (!force && _appList.Count > 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _scanBusy, 1) == 1)
        {
            // A scan is already running. Recorded so it runs again afterwards
            // rather than being dropped: a force:true caller - the scan button -
            // wants a fresh look, and returning without saying so would leave it
            // with the previous list and no indication that nothing happened.
            _appListPending = true;
            return;
        }

        try
        {
            // Cleared *before* each pass, not after the last one. The flag means
            // "somebody asked while this was in flight", so a caller arriving
            // during the scan sets it and the loop runs one more time; a caller
            // arriving during the final check finds it clear and is served by that
            // pass. Setting it inside the body instead - which is what this did
            // first - means it is always true when the condition is read, so the
            // scan runs at full speed for the life of the process.
            while (true)
            {
                _appListPending = false;

                _appList = (await System.Threading.Tasks.Task.Run(() => _library.Scan()))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!_appListPending)
                {
                    break;
                }
            }

            // In the background, and only once the scan has settled. This is the
            // pass that makes the dropdowns open with their icons already on them.
            WarmAppIcons();
        }
        finally
        {
            _appListPending = false;
            Interlocked.Exchange(ref _scanBusy, 0);
        }
    }

    /// <summary>Set by a caller that arrived while a scan was already running.</summary>
    private bool _appListPending;


    private async void OnScanAppsClick(object sender, RoutedEventArgs e)
    {
        // Disabled for the duration rather than merely guarded, because this is
        // the slowest thing the app does on demand and the only feedback it gave
        // was one line of caption text. The flag above stops the duplicate work;
        // this stops the button looking available while it is happening.
        ScanGamesButton.IsEnabled = false;
        RailStatus.Text = "SCANNING FOR GAMES";

        try
        {
            await EnsureAppList(force: true);
            BuildSlots();
            RailStatus.Text = "READY";
            Flash(_appList.Count.ToString(CultureInfo.InvariantCulture) + " games found");
        }
        catch (Exception ex)
        {
            TraceLog.Write("SCAN GAMES", ex);
            RailStatus.Text = "SCAN FAILED";
            Flash("Could not scan for games", true);
        }
        finally
        {
            ScanGamesButton.IsEnabled = true;
        }
    }


    /// <param name="screenOnly">
    /// Push the screen only and leave the sound alone. Used when the screen is being
    /// restored from a focus pause: the sound was never taken down, so pushing it
    /// again would spawn the engine and re-apply a tune that is already loaded, on
    /// every return from the pause - and a return is every time the user alt-tabs
    /// back to check something.
    /// </param>
    private void PlaySlot(HotkeySlot slot, bool announce, bool screenOnly = false)
    {
        DisplayPreset? display = FindDisplay(slot.DisplayPresetId);
        AudioPreset? audio = FindAudio(slot.AudioPresetId);

        if (display is null && audio is null)
        {
            // There is nothing to load, so nothing is about to be loaded. Saying
            // otherwise left the status bar and the toast claiming a preset that
            // was never applied, and left the key looking bound to something when
            // it was bound to nothing at all.
            RailStatus.Text = "NOTHING SET";
            Flash("Nothing set on " + slot.Name, true);
            return;
        }

        bool screenTookIt = true;
        if (display is not null)
        {
            LoadTune(display.Copy(), (audio ?? _workAudio).Copy());
            screenTookIt = ApplyDisplay(display.Copy(), slot.MonitorDevice, false);
        }

        // Announced before the slow half, not after it.
        //
        // Everything this toast reports as done happens above and below this line:
        // a gamma ramp, a DDC/CI call that takes its time over I2C, and a profile
        // write to disk. The plate used to go up once all of that had finished, so
        // a slot key did nothing visible for a beat and then the toast appeared -
        // which reads as the keypress having been missed rather than as the app
        // answering it. Saying it up front means the confirmation lands inside the
        // same gesture.
        //
        // It is a claim, not a report, and it is corrected below if the screen
        // refuses. The correction is a second toast rather than a retraction
        // because there is no way to unsay the first one, and an amber plate
        // arriving a moment after a grey one says "that did not work" in a way the
        // user is already used to reading.
        RailStatus.Text = slot.Name.ToUpperInvariant();
        if (announce)
        {
            Flash(slot.Name + " loaded");
        }

        if (audio is not null && !screenOnly)
        {
            ApplyAudioToDevice(audio.Copy(), false, slot);
        }

        // The sound half is applied either way. A slot is one gesture covering two
        // independent things, and a display that refuses a gamma ramp is no reason
        // to leave the audio preset the user asked for unapplied as well. What
        // changes is only what gets claimed afterwards: the rail and the toast
        // describe the whole slot, so they say the screen did not take it rather
        // than reporting a clean load for something half of which is missing.
        if (!screenTookIt)
        {
            RailStatus.Text = "SCREEN BLOCKED";

            if (announce)
            {
                Flash(slot.Name + " loaded, screen did not take it", true);
            }
        }
    }


    private void OnForegroundChanged(WatchedWindow window)
    {
        if (!_settings.AutoSwitch)
        {
            return;
        }

        // Back on something real, so any pending pause is off. Before the match,
        // because the returning window may be a different program entirely - a
        // browser rather than the game - and a pause armed by the last alt-tab
        // must not survive the user coming back to read a message.
        CancelFocusPause();

        // A specific match always wins over the wildcard. The wildcard is a
        // fallback for a game nobody remembered to bind, not a competitor - if it
        // were checked first, every bound game would be at the mercy of list order.
        HotkeySlot? slot = SlotService.MatchForeground(_settings.Slots, window.ExePath, window.ProcessName);

        bool wildcard = false;
        if (slot is null)
        {
            slot = SlotService.MatchWildcard(_settings.Slots);
            wildcard = slot is not null;

            // The fullscreen test, here rather than in the matcher because it needs
            // the window handle and the matcher is handed strings. Without it the
            // wildcard applies to every window the user alt-tabs to, which is
            // worse than not having the feature: it would take over the picture of
            // a browser and every document they open.
            if (slot is not null && !IsFullscreen(window))
            {
                TraceLog.Write("AUTO WILDCARD skip " + window.ProcessName + " (not fullscreen)");
                RevertWildcardOnFocusLoss(window);
                return;
            }
        }

        if (slot is null || !ShouldAutoApply(slot, window.ProcessName))
        {
            return;
        }

        TraceLog.Write((wildcard ? "AUTO WILDCARD " : "AUTO FOCUS ") + slot.Name + " <- " + window.ExePath);

        if (wildcard)
        {
            // Remembered so the revert knows what to undo. The wildcard matched a
            // process rather than a slot's own game, and OnTargetExited looks the
            // slot up by process name - which finds nothing here, because this
            // process is not bound to anything. Without this the profile would be
            // left boosted for whatever the user ran next.
            _autoWildcardProcess = window.ProcessName;

            // And registered with the watcher, which is the other half of that.
            // OnTargetExited only ever hears about a process the scan considers
            // alive-and-then-gone, and a wildcard contributes nothing to the watch
            // list, so without this its exit was never noticed at all. The revert
            // was fully written and simply never ran.
            //
            // The refusal is checked rather than dropped. An empty return means a
            // different process is still being watched, so this game is applied but
            // unmonitored and its exit will never revert anything - which used to
            // be recorded nowhere at all.
            if (_watcher.WatchUnbound(window.ProcessName).Length == 0)
            {
                TraceLog.Write("WATCH refused " + window.ProcessName + " <- already watching a different process");
            }
        }

        // The screen is coming back rather than being applied fresh. Without this
        // the sound would be pushed again on every return from a pause, and a return
        // is not a rare event - it is every time the user alt-tabs back to check
        // something.
        bool resuming = string.Equals(_pausedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase);

        if (resuming)
        {
            TraceLog.Write("AUTO FOCUS resume " + slot.Name);
        }

        PlaySlot(slot, true, screenOnly: resuming);

        if (resuming)
        {
            ClearFocusPause();
        }
    }

    /// <summary>
    /// Arms the screen pause, if a pause is warranted at all.
    /// </summary>
    /// <remarks>
    /// Called from two places: a real window taking the foreground, and the watcher
    /// saying the shell has it. The two differ because the first can also be
    /// <em>back</em> to the game, and returning is not leaving - which is why the
    /// match is on the foreground process name rather than merely "somebody else is
    /// in front".
    /// </remarks>
    private void ConsiderFocusPause(string foregroundProcess)
    {
        if (!_settings.AutoSwitch || !_settings.AutoPauseOnFocusLoss)
        {
            return;
        }

        if (!ShouldPauseForFocusLoss(
                enabled: true,
                autoSwitch: true,
                autoSlotId: _autoSlotId,
                wildcardProcess: _autoWildcardProcess,
                autoProcess: _autoProcess,
                foregroundProcess: foregroundProcess))
        {
            CancelFocusPause();
            return;
        }

        if (_focusPause is not null)
        {
            // Already armed. Not restarted: a timer that restarted on every tick
            // would never fire while the user sat still, which is the one case the
            // pause is for.
            return;
        }

        _focusPause = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(AppSettings.FocusPauseGraceSeconds)
        };

        _focusPause.Tick += OnFocusPauseTick;
        _focusPause.Start();
    }

    /// <summary>
    /// Whether the screen should be paused because something other than the game is
    /// in front.
    /// </summary>
    /// <remarks>
    /// Pure, so the whole decision can be tested without a desktop, a display or a
    /// timer. Three of the six conditions are exclusions that each prevent a
    /// specific wrong answer:
    /// <para>
    /// No auto-slot means nothing of ours is loaded, so there is nothing to pause.
    /// </para>
    /// <para>
    /// A wildcard process means the existing wildcard revert owns this case. It
    /// already reverts on focus loss, on its own terms, and arming a second
    /// mechanism alongside it would have two answers to one question.
    /// </para>
    /// <para>
    /// The same process name means the user is still in the game - another window
    /// of it, or a second instance. Pausing then would suspend the boost for
    /// looking at the game's own launcher.
    /// </para>
    /// </remarks>
    internal static bool ShouldPauseForFocusLoss(
        bool enabled,
        bool autoSwitch,
        string autoSlotId,
        string wildcardProcess,
        string autoProcess,
        string foregroundProcess)
    {
        if (!enabled || !autoSwitch)
        {
            return false;
        }

        if (autoSlotId.Length == 0)
        {
            return false;
        }

        if (wildcardProcess.Length > 0)
        {
            return false;
        }

        // Empty is "nothing real in front", not "the user went somewhere else". Read
        // returns null in several cases that are not the desktop - a process that
        // would not hand over its image path, an exception in the read itself - and
        // pausing on any of those would suspend a boost for no reason at all.
        if (foregroundProcess.Length == 0)
        {
            return false;
        }

        return !string.Equals(foregroundProcess, autoProcess, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The slot whose screen was paused, so it can be told apart from the slot the
    /// app is currently applying.
    /// </summary>
    /// <remarks>
    /// Separate from <c>_autoSlotId</c> rather than reusing it, because the pause
    /// stands the screen down and <c>StandDownScreen</c> clears <c>_autoSlotId</c> on
    /// its way through. Storing it here is what lets the exit path still find the
    /// slot after a pause, which is the case that would otherwise leave the sound
    /// boosted once the game closed.
    /// </remarks>
    private string _pausedSlotId = string.Empty;

    /// <summary>Which process the paused slot was loaded for.</summary>
    private string _pausedProcess = string.Empty;

    /// <summary>One shot, armed by leaving and fired by still being away.</summary>
    private DispatcherTimer? _focusPause;

    /// <summary>
    /// The grace period ran out and the user is still not in the game.
    /// </summary>
    private void OnFocusPauseTick(object? sender, EventArgs e)
    {
        CancelFocusPause();

        if (_quitting || _autoSlotId.Length == 0)
        {
            // Quitting, or already stood down by something else in the meantime.
            // _autoSlotId is the authority rather than the armed slot id, because
            // it is the field the app itself set and the question is whether
            // anything of ours is still loaded.
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, _pausedSlotId, StringComparison.OrdinalIgnoreCase));

        if (slot is null)
        {
            // Deleted or disabled while paused. Nothing to pause and nothing to say.
            ClearFocusPause();
            return;
        }

// Taken into locals first. StandDownScreen empties _autoSlotId AND
        // _autoProcess on its way through, so reading them after the call to
        // restore them would store the empty strings - and the exit path would
        // then fail to match the paused game's process, which is the one thing
        // this record exists for.
        string slotId = _autoSlotId;
        string process = _autoProcess;

        _pausedSlotId = slotId;
        _pausedProcess = process;

        GoScreenStandDown();

        // Restored afterwards, because StandDownScreen also clears a pause that is
        // still armed - correct for every other caller, since a user taking the
        // screen back means the pause is over, but here it would take the record
        // this path depends on.
        _pausedSlotId = slotId;
        _pausedProcess = process;

        TraceLog.Write("AUTO FOCUS paused " + slot.Name + " after "
            + AppSettings.FocusPauseGraceSeconds + "s away");
        Flash(slot.Name + " paused while you are away");
    }

    /// <summary>Disarms a pending pause. Does not un-pause an already paused screen.</summary>
    private void CancelFocusPause()
    {
        if (_focusPause is null)
        {
            return;
        }

        _focusPause.Stop();
        _focusPause.Tick -= OnFocusPauseTick;
        _focusPause = null;
    }

    /// <summary>Forgets the pause entirely, used when the screen is back.</summary>
    private void ClearFocusPause()
    {
        CancelFocusPause();
        _pausedSlotId = string.Empty;
        _pausedProcess = string.Empty;
    }

    /// <summary>
    /// The watcher says the desktop or the shell has the foreground.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OnForegroundChanged"/> because that one is never
    /// called for this at all - the shell processes are filtered out of
    /// <c>Read()</c>, which is why <c>ForegroundLeft</c> exists.
    /// </remarks>
    private void OnForegroundLeft(string shell)
    {
        if (!_settings.AutoSwitch || !_settings.AutoPauseOnFocusLoss)
        {
            return;
        }

        // Nothing of ours loaded, or the wildcard owns this case already.
        if (_autoSlotId.Length == 0 || _autoWildcardProcess.Length > 0)
        {
            return;
        }

        _pausedSlotId = _autoSlotId;
        TraceLog.Write("AUTO FOCUS left to " + shell + ", arming the pause");
        ConsiderFocusPause(shell);
    }

    /// <summary>
    /// Whether the window covers its whole monitor.
    /// <para>
    /// The shape that reliably means a game is running. A maximised browser covers
    /// the work area and leaves the taskbar visible, so comparing against the work
    /// area would match half the desktop; comparing against the monitor rectangle
    /// is what distinguishes the two, and it is the same distinction the gamma lock
    /// has to make before deciding not to fight something on screen.
    /// </para>
    /// <para>
    /// Fails closed. If the window's own rectangle cannot be read - which happens
    /// on a desktop that is being torn down, and for windows owned by another
    /// user's session - the answer is no, so the wildcard stands down rather than
    /// guessing.
    /// </para>
    /// </summary>
    private static bool IsFullscreen(WatchedWindow window)
    {
        return window.IsFullscreen;
    }

    /// <summary>
    /// The wildcard's game is still running but the user has left it, so the preset
    /// goes back to neutral.
    /// <para>
    /// The other half of the fullscreen rule, and it was missing. The rule answers
    /// "when should this apply", and until now nothing answered "when should it
    /// stop" - so alt-tabbing to a browser left the game profile on a browser, and
    /// the wildcard would not re-apply either because the browser is not fullscreen.
    /// The screen stayed wrong until the user pressed something.
    /// </para>
    /// <para>
    /// Gated on this being the wildcard's own application. Reverting because the
    /// user alt-tabbed into some unrelated fullscreen window would undo a preset
    /// they had chosen deliberately, and a game that owns a bound slot has its own
    /// exit handling which does not work this way.
    /// </para>
    /// <para>
    /// Deliberately does not release the watch. The game is still running, so
    /// quitting it should still revert - and if the user alt-tabs back in, the
    /// wildcard applies again on the normal path.
    /// </para>
    /// </summary>
    private void RevertWildcardOnFocusLoss(WatchedWindow window)
    {
        if (_autoWildcardProcess.Length == 0)
        {
            return;
        }

        if (string.Equals(_autoWildcardProcess, window.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // Back to the game, or another window of it. Not a focus loss.
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, _autoSlotId, StringComparison.OrdinalIgnoreCase));

        if (slot is null || !slot.IsAnyGameTarget)
        {
            return;
        }

        if (!_settings.AutoRevertOnExit || _quitting)
        {
            return;
        }

        TraceLog.Write("AUTO WILDCARD focus left " + _autoWildcardProcess
            + " <- " + window.ProcessName);

        // The apply debounce is cleared inside GoScreenStandDown, which is where all four
        // stand-down paths now clear it. This used to be a second site, and the two
        // did not agree.

        // A stand-down rather than a hard reset: the game is over, not the screen
        // broken. Same reasoning as the exit path in OnTargetExited - the panel
        // goes back to where the user had it, unless this preset never touched it.
        GoScreenStandDown();
        _ = GoSoundNeutralAsync();

        Flash(slot.Name + " left behind, back to normal");
    }


    private void OnTargetLaunched(WatchedWindow window)
    {
        if (!_settings.AutoSwitch)
        {
            return;
        }

        HotkeySlot? slot = SlotService.MatchProcessName(_settings.Slots, window.ProcessName);
        if (slot is not null && ShouldAutoApply(slot, window.ProcessName))
        {
            TraceLog.Write("AUTO LAUNCH " + slot.Name + " <- " + window.ExePath);
            PlaySlot(slot, true);
        }
    }


    /// <summary>
    /// A game the app loaded a slot for has closed, so the screen and sound go
    /// back to neutral rather than staying boosted for whatever comes next.
    /// <para>
    /// Only ever undoes a slot this app applied by itself. The guard is the id
    /// left behind by the auto-apply that matched, so if the user has since
    /// pressed Reset, hit the slot's own key, or loaded a different tune, there is
    /// nothing to undo and the exit is ignored. Without that, quitting a game
    /// would stomp a preset the user had chosen on purpose.
    /// </para>
    /// </summary>
    private void OnTargetExited(string processName)
    {
        // The paused case, and it has to come first. A focus pause stands the
        // screen down, and StandDownScreen clears _autoSlotId on its way through -
        // so by the time a paused game's process exits, MatchProcessName finds the
        // slot but the guard at the bottom ("this is the slot the app applied")
        // does not, because _autoSlotId is empty. The exit would be ignored and the
        // SOUND would stay boosted for whatever the user runs next, which is the
        // one thing the whole exit path exists to prevent.
        //
        // Sound only: the screen is already neutral, so pushing it again would be
        // doing work that is not needed.
        if (_pausedSlotId.Length > 0
            && string.Equals(_pausedProcess, processName, StringComparison.OrdinalIgnoreCase))
        {
            TraceLog.Write("AUTO EXIT while paused " + processName);
            ClearFocusPause();

            if (_settings.AutoRevertOnExit && !_quitting)
            {
                _ = GoSoundNeutralAsync();
                Flash("Game closed, back to normal");
            }

            return;
        }

        if (!_settings.AutoRevertOnExit || _quitting)
        {
            return;
        }

        HotkeySlot? slot = SlotService.MatchProcessName(_settings.Slots, processName);
        if (slot is null)
        {
            // Not a bound game. It may still be a program a wildcard slot applied
            // itself for, which MatchProcessName cannot find by construction - the
            // wildcard matched something that is not bound to anything.
            //
            // Both halves have to agree: the process must be the one remembered at
            // apply time, and the slot that applied it must still be the one
            // loaded. Without the first, quitting any unbound program would revert
            // the profile; without the second, a revert would fire for an
            // application the user has since overridden with their own key press.
            if (!string.Equals(_autoWildcardProcess, processName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            slot = _settings.Slots.FirstOrDefault(s =>
                string.Equals(s.Id, _autoSlotId, StringComparison.OrdinalIgnoreCase));

            if (slot is null || !slot.IsAnyGameTarget)
            {
                TraceLog.Write("AUTO WILDCARD EXIT SKIP " + processName + " <- no longer the loaded slot");
                return;
            }
        }

        if (!string.Equals(_autoSlotId, slot.Id, StringComparison.OrdinalIgnoreCase))
        {
            TraceLog.Write("AUTO EXIT SKIP " + processName + " <- not the auto loaded slot");
            return;
        }

        TraceLog.Write("AUTO EXIT " + slot.Name + " <- " + processName);

        // Released explicitly rather than left to the watcher's own exit handling,
        // which already removed it before raising this. Named here so the two
        // paths - the wildcard's exit and the wildcard's focus loss - release the
        // same thing, and so a second exit for the same process cannot be raised
        // by a name that is no longer being watched.
        _watcher.ForgetUnbound(processName);

        // GoScreenStandDown stands the auto-apply guard down as it goes, so a second
        // exit for the same slot cannot fire a second revert. And the panel is put
        // back rather than left at the game's brightness, because the desktop the
        // user is returning to is theirs and should be at the brightness they set
        // - but only if this preset was what moved it.
        GoScreenStandDown();

        // Off the dispatcher. This is three engine calls that each wait on a child
        // process, and it runs at the moment a game exits, which is the worst
        // possible time to stop the window answering. GoSoundNeutralAsync puts its
        // own window work back on the dispatcher before it returns.
        _ = GoSoundNeutralAsync();

        Flash(slot.Name + " closed, back to normal");
    }


    private bool ShouldAutoApply(HotkeySlot slot, string processName)
    {
        if (string.Equals(_autoSlotId, slot.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_autoProcess, processName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AutoApplyGate.IsDebounced(_autoStamp, DateTime.UtcNow))
        {
            return false;
        }

        _autoStamp = DateTime.UtcNow;
        _autoSlotId = slot.Id;
        _autoProcess = processName ?? string.Empty;

        // A wildcard application is remembered against its process only. A slot
        // applied for a bound game is undone through OnTargetExited's own lookup,
        // which finds it by name, and a wildcard's name matches nothing - so the
        // two cannot share a field without one of them reading the other's answer.
        _autoWildcardProcess = slot.IsAnyGameTarget ? _autoProcess : string.Empty;
        return true;
    }


    /// <summary>
    /// Registers every armed slot, plus the global off key.
    ///
    /// The duplicate rule lives here rather than in the key capture handler on
    /// purpose. Capture only checks the slots that are armed at that moment, so a
    /// key can still end up shared: disable a slot, give its key to another one,
    /// then re-arm the first. A restored backup can do the same thing without
    /// anyone typing. Enforcing it in one place means the invariant holds however
    /// the state got that way, first slot wins, and the loser is named so the
    /// silent "KEY BUSY" in the status bar becomes something you can act on.
    /// </summary>
    private void RegisterHotkeys()
    {
        _hotkeys.Clear();
        HashSet<string> taken = new(StringComparer.Ordinal);
        List<HotkeySlot> skipped = new();

        // Keys some other program already owns, which is a different problem from
        // a duplicate in here and gets its own wording.
        HashSet<string> refusedKeys = new(StringComparer.OrdinalIgnoreCase);
        int id = 1;

        // The panic key is offered to Windows before any slot, so that if the two
        // ever meet it is the slot that gives way. It is the one binding whose
        // entire job is to still work when something else has gone wrong, and a
        // slot is a shortcut. A slot that loses the combo is reported below like
        // any other duplicate rather than silently going dead.
        string panicKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(_settings.EmergencyHotkey))
        {
            panicKey = HotkeyService.Normalise(_settings.EmergencyHotkey);

            // Checked, and claimed only on success, exactly as a slot is below.
            // This threw the answer away, and that made two things wrong at once
            // when another program owned the combo: the key stayed in `taken`, so
            // the slot holding it was skipped and then reported as "the panic key
            // took CTRL+ALT+F12 from <slot>" - a claim that was false, because the
            // panic key was not registered either. Both bindings were dead and the
            // message named the wrong one.
            if (_hotkeys.Register(EmergencyHotkeyId, EmergencyTargetId, _settings.EmergencyHotkey))
            {
                taken.Add(panicKey);
            }
            else
            {
                panicKey = string.Empty;
            }
        }

        // The bypass key is offered to Windows after the panic key and before any slot,
        // so a contested chord goes to the panic key first and then to the bypass
        // key, and only a slot ever gives way. The ordering is the whole rule and it
        // is here rather than in a helper because "which of these three wins" is
        // only ever asked in one place.
        if (!string.IsNullOrWhiteSpace(_settings.BypassHotkey))
        {
            string bypassKey = HotkeyService.Normalise(_settings.BypassHotkey);

            // Claimed only on success, as the panic key above does. Claiming it
            // first and releasing it on failure skips the slot holding it and then
            // reports the bypass key as having taken it, which is false and is the
            // bug the panic key's own comment describes.
            if (taken.Add(bypassKey))
            {
                if (!_hotkeys.Register(BypassHotkeyId, BypassTargetId, _settings.BypassHotkey))
                {
                    taken.Remove(bypassKey);
                    refusedKeys.Add(_settings.BypassHotkey);
                }
            }
            else
            {
                // The panic key already has this chord. Reported as its own case
                // rather than silently dead, because BindBypassHotkey refuses to
                // create this state and a restored profile can still arrive with it.
                skipped.Add(new HotkeySlot { Name = "bypass key" });
            }
        }

        foreach (HotkeySlot slot in _settings.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.Hotkey) && slot.Enabled)
            {
                string key = HotkeyService.Normalise(slot.Hotkey);
                if (taken.Add(key))
                {
                    // The answer matters. RegisterHotKey fails when some other
                    // program already owns the combo system wide, and the failure
                    // was thrown away here: the slot counted as bound, its keycap
                    // kept rendering the shortcut, and nothing in the list showed
                    // that pressing it would do nothing. The only sign was one
                    // status line naming the key rather than the slot, which the
                    // next apply or reset overwrote.
                    if (!_hotkeys.Register(id, "slot:" + slot.Id, slot.Hotkey))
                    {
                        taken.Remove(key);
                        skipped.Add(slot);
                        refusedKeys.Add(slot.Hotkey);
                    }
                }
                else
                {
                    skipped.Add(slot);
                }
            }

            id++;
        }

        if (skipped.Count > 0)
        {
            string names = string.Join(", ", skipped.Select(s => s.Name));
            string keys = string.Join(", ", skipped.Select(s => s.Hotkey).Distinct(StringComparer.OrdinalIgnoreCase));
            bool lostToPanic = panicKey.Length > 0
                && skipped.Any(s => HotkeyService.Normalise(s.Hotkey) == panicKey);

            // Refused by the operating system rather than by this app. Said
            // separately, because the user's next move is different: close the
            // other program, rather than free up a duplicate in here.
            bool refusedByOs = refusedKeys.Count > 0;

            RailStatus.Text = (refusedByOs ? "KEY HELD BY ANOTHER PROGRAM ON " : "DUP KEY SKIPPED ON ")
                + names.ToUpperInvariant();

            Flash(
                refusedByOs
                    ? "Another program already owns " + string.Join(", ", refusedKeys) + ", so " + names + " will not fire"
                    : lostToPanic
                        ? "Panic key took " + keys + " from " + names
                        : "Key already on another slot, skipped on " + names,
                true);

            TraceLog.Write("HOTKEY " + (refusedByOs ? "refused by the system" : "duplicate")
                + " " + keys + " on " + names);
        }
    }


    /// <summary>
    /// A slot key is a toggle: the first press loads the slot, and pressing the
    /// same key again takes it back off again, which puts the screen and the
    /// sound back to their neutral presets. There is no separate off key, so
    /// every slot only ever needs one binding and the gesture that loaded a
    /// setup is also the gesture that backs it out.
    ///
    /// The test is which preset is live rather than a remembered flag, so the
    /// toggle still behaves if a slot was loaded by auto-switch or by clicking
    /// the row instead of by pressing the key.
    ///
    /// Holding the key down cannot toggle twice and undo the work, because every
    /// binding is registered with MOD_NOREPEAT, so Windows delivers one press
    /// per physical press rather than a stream of repeats.
    /// </summary>
    private async void OnHotkeyPressed(HotkeyBinding binding)
    {
        if (string.Equals(binding.TargetId, BypassTargetId, StringComparison.OrdinalIgnoreCase))
        {
            // Through the switch rather than through the setting. BypassToggle's
            // handler is the one place that sets EffectsEnabled, commits, queues the
            // engine push and verifies the result landed - four things that all have
            // to happen together or the switch and the engine disagree.
            //
            // The inversion is BypassToggle's own and must not be second guessed here:
            // the switch is labelled BYPASS, so IsChecked == true means the effects
            // are OFF. AppSettings.EffectsEnabled says the same thing the other way
            // round, and the two disagreeing by design is documented there.
            if (!_audio.IsInstalled)
            {
                Flash("Sound needs FxSound first", true);
                return;
            }

            BypassBox.IsChecked = BypassBox.IsChecked != true;
            Flash(BypassBox.IsChecked == true ? "Sound effects off" : "Sound effects on");
            TraceLog.Write("BYPASS key pressed, effects "
                + (BypassBox.IsChecked == true ? "off" : "on"));
            return;
        }

        if (string.Equals(binding.TargetId, EmergencyTargetId, StringComparison.OrdinalIgnoreCase))
        {
            // The same neutral path a slot takes when it is switched off, so the
            // panic key lands in exactly the state quitting would have left, and
            // there is only one definition of "back to normal" in the app.
            //
            // emergency: true is what makes this the one path that answers in full.
            // The panic key exists for a screen that is already wrong and a user who
            // cannot click anything, so "the night filter is still scheduled" has to
            // lose to "I can see what is happening". Every other stand-down - the
            // tray, the reset button, a game closing - leaves the filter alone.
            GoScreenNeutral(emergency: true);
            SuppressNightUntilNextWindow();
            await GoSoundNeutralAsync();
            RailStatus.Text = "PANIC RESET";
            Flash(_nightSuppressed && _settings.NightBlueLight
                ? "Screen and sound reset, night filter paused"
                : "Screen and sound reset");
            TraceLog.Write("PANIC key pressed, screen and sound reset, night filter "
                + (_nightSuppressed ? "suppressed" : "untouched"));
            return;
        }

        if (binding.TargetId.StartsWith("slot:", StringComparison.OrdinalIgnoreCase))
        {
            string id = binding.TargetId["slot:".Length..];
            HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == id);
            if (slot is null)
            {
                return;
            }

            await ToggleSlot(slot);
        }
    }


    /// <summary>
    /// Loads a slot, or takes it back off again if it is the one that is already
    /// loaded.
    /// <para>
    /// Out here so the tray and the key are the same gesture. The tray menu can
    /// load a slot, and the first version of that wired it straight to
    /// <see cref="PlaySlot"/>, which has no off switch: choosing the slot you
    /// were already on would reload it and say so, where the same choice made
    /// with the key would have put the screen and the sound back to normal. Two
    /// ways of asking the same question with two answers, one of which looks
    /// like a bug to anybody who tries the other one first.
    /// </para>
    /// <para>
    /// Which preset is live decides which way it goes, not a remembered flag, so
    /// this still behaves for a slot that was loaded by auto-switch or by
    /// clicking its row on the board.
    /// </para>
    /// </summary>
    private async Task ToggleSlot(HotkeySlot slot)
    {
        if (!slot.HasWork)
        {
            RailStatus.Text = "NOTHING SET";
            Flash("Nothing set on " + slot.Name, true);
            return;
        }

        if (SlotService.IsLoaded(slot, _activeDisplayId, _activeAudioId))
        {
            // A stand-down, not a hard reset. The user is turning a preset off, not
            // asking for the whole screen rebuilt, so the panel is put back only if
            // the preset they were running was the thing that dimmed it.
            GoScreenStandDown();
            await GoSoundNeutralAsync();
            Flash(slot.Name + " off");
            return;
        }

        PlaySlot(slot, true);
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


    /// <summary>
    /// One of the two keycaps that are not a slot's, wired for capture.
    /// <para>
    /// The panic key and the bypass key behave identically apart from which
    /// setting they write to and what they are called in a message, so they share
    /// this rather than having a bind and a clear and a focus pair each. A third
    /// keycap later is one line here.
    /// </para>
    /// <para>
    /// Both keycaps set the shared capture state to themselves, which is what stops
    /// two of them being armed at once: a keycap only starts listening when it has
    /// keyboard focus, and only one control can.
    /// </para>
    /// </summary>
    /// <param name="box">The keycap, declared in the page.</param>
    /// <param name="which">Which of the two this is.</param>
    private void WireKeycap(TextBox box, KeycapCapture which)
    {
        Func<string> read = () => which == KeycapCapture.Panic
            ? _settings.EmergencyHotkey
            : _settings.BypassHotkey;

        Describe(box, read());

        box.GotKeyboardFocus += (s, e) =>
        {
            _capturingKeycap = which;
            _captureSlotId = null;
            _captureBox = box;
            box.Text = "PRESS A KEY";
            box.ToolTip = "Esc cancels  ·  Backspace clears  ·  F1 to F24 bind on their own";
        };
        box.LostKeyboardFocus += (s, e) =>
        {
            if (!ReferenceEquals(_captureBox, box))
            {
                return;
            }

            // Clicked away without choosing anything, so it keeps what it had.
            Describe(box, read());
            _captureBox = null;
            _capturingKeycap = KeycapCapture.None;
        };
    }

    /// <summary>The name a keycap uses in a message.</summary>
    private static string LabelOf(KeycapCapture which) =>
        which == KeycapCapture.Panic ? "Panic key" : "Bypass key";

    /// <summary>
    /// What a non-slot keycap shows when it is not listening.
    /// <para>
    /// Goes through <see cref="ShowSlotKey"/> rather than a second implementation,
    /// because the two keycaps and every slot keycap have to read the same way and a
    /// copy of this is a copy that will drift.
    /// </para>
    /// </summary>
    private static void Describe(TextBox box, string hotkey) => ShowSlotKey(box, hotkey);

    /// <summary>
    /// Points the panic key at a new combo, taking it off any slot that held it.
    /// The steal is the same rule the slots use, the thing you are pointing at
    /// wins, because a slot quietly keeping a chord the user has just given away
    /// is how you end up with two things bound to one key and no way to tell.
    /// </summary>
    private void BindPanicHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);

        // The bypass key gives way rather than the panic key. The panic key is the
        // one binding whose entire job is to still work when something else has gone
        // wrong, and a bypass is a convenience - so this is the same precedence
        // RegisterHotkeys applies, kept in one place so the two cannot disagree.
        if (string.Equals(HotkeyService.Normalise(_settings.BypassHotkey), wanted, StringComparison.Ordinal))
        {
            _settings.BypassHotkey = string.Empty;
            Flash("Panic key took it from the bypass key");
        }

        HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
            s.Enabled
            && string.Equals(HotkeyService.Normalise(s.Hotkey), wanted, StringComparison.Ordinal));

        if (clash is not null)
        {
            clash.Hotkey = string.Empty;
            Flash("Panic key took it from " + clash.Name);
        }

        _settings.EmergencyHotkey = text;
        EndCapture(restore: false);
        Commit();
        ShowSlotKey(PanicKeyBox, text);
        BuildSlots();
        RegisterHotkeys();
        Flash("Panic key set to " + text);
    }


    /// <summary>
    /// Points the bypass key at a new combo, taking it off any slot that held it.
    /// <para>
    /// Same rule as the panic key, and deliberately weaker: the bypass key wins
    /// against a slot because the user is pointing at it, and loses against the
    /// panic key because the panic key has to keep working when something has gone
    /// wrong. It is refused outright if it is already the panic key's, rather than
    /// taking it - two controls bound to one chord is the state this whole method
    /// exists to prevent.
    /// </para>
    /// </summary>
    private void BindBypassHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);

        if (string.Equals(HotkeyService.Normalise(_settings.EmergencyHotkey), wanted, StringComparison.Ordinal))
        {
            // Refused rather than bound. Stealing it here would leave the bypass
            // keycap showing a chord that does nothing, and the user would have no
            // way to tell which of the two keycaps is lying.
            EndCapture(restore: true);
            Flash("That is the panic key already", true);
            return;
        }

        HotkeySlot? clash = _settings.Slots.FirstOrDefault(s =>
            s.Enabled
            && string.Equals(HotkeyService.Normalise(s.Hotkey), wanted, StringComparison.Ordinal));

        if (clash is not null)
        {
            clash.Hotkey = string.Empty;
            Flash("Bypass key took it from " + clash.Name);
        }

        _settings.BypassHotkey = text;
        EndCapture(restore: false);
        Commit();
        ShowSlotKey(BypassKeyBox, text);
        BuildSlots();
        RegisterHotkeys();
        Flash("Bypass key set to " + text);
    }


    /// <summary>Backspace or Delete on the panic keycap drops the binding.</summary>
    private void ClearPanicHotkey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EmergencyHotkey))
        {
            _settings.EmergencyHotkey = string.Empty;
            Flash("Panic key cleared");
        }

        EndCapture(restore: false);
        Commit();
        ShowSlotKey(PanicKeyBox, _settings.EmergencyHotkey);
        RegisterHotkeys();
    }


    /// <summary>Backspace or Delete on the bypass keycap drops the binding.</summary>
    private void ClearBypassHotkey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.BypassHotkey))
        {
            _settings.BypassHotkey = string.Empty;
            Flash("Bypass key cleared");
        }

        EndCapture(restore: false);
        Commit();
        ShowSlotKey(BypassKeyBox, _settings.BypassHotkey);
        RegisterHotkeys();
    }


    /// <summary>
    /// Gives the panic keycap the same behaviour as a slot keycap: click to
    /// listen, Esc cancels, Backspace clears, never free text. Wired once here
    /// rather than rebuilt with the rest of the page, because the box is declared
    /// in the page and lives as long as the window does.
    /// </summary>
    private void WirePanicKeycap()
    {
        WireKeycap(PanicKeyBox, KeycapCapture.Panic);
    }


    /// <summary>The bypass keycap, which is the panic keycap with another target.</summary>
    private void WireBypassKeycap()
    {
        WireKeycap(BypassKeyBox, KeycapCapture.Bypass);
    }


    private void OnHotkeyFailed(string text)
    {
        RailStatus.Text = "KEY BUSY " + text;
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
