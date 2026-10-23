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

// Where a slot sends its picture and its sound: its output device and the screen it covers.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
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

        // Resolved through the service rather than matched here, so the row shows
        // the same screen the slot will actually be applied to. A name that no
        // longer belongs to the saved panel resolves to nothing and the box sits
        // on "All screens", which is the same way a screen that has been
        // unplugged has always been shown.
        string resolved = _display.ResolveDevice(slot.MonitorKey, slot.MonitorDevice);
        int index = 0;
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Device, resolved, StringComparison.OrdinalIgnoreCase))
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
        slot.MonitorKey = choice.Key;
        Commit();
    }
}
