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

// Slot board: the per-slot pickers - the screen, sound and crosshair dropdowns,
// the game picker handler, and the launch options dialog.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>
    /// The extra words Play hands to this slot's game.
    /// <para>
    /// A dialog rather than a column, for the reason the row already gives about HDR:
    /// it is measured to the pixel, and most slots never need this. A slot that
    /// starts through a launcher is told so here, in the field's own caption, because
    /// the launcher owns the command line of the game it starts and a field that took
    /// the text and silently dropped it would be worse than no field.
    /// </para>
    /// </summary>
    private void ShowLaunchOptionsDialog(HotkeySlot slot)
    {
        StackPanel content = new();

        content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            Text = "Anything the game takes on its command line, as you would type it after the exe: -dx12 -skipintro. Leave it empty for none.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        TextBox field = new()
        {
            Style = (Style)FindResource("ModernTextBox"),
            Text = slot.LaunchArguments,
            MaxLength = LaunchArguments.Limit,
            Margin = new Thickness(0, 12, 0, 0),
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(field, "Launch arguments for " + slot.Name);
        content.Children.Add(field);

        bool viaLauncher = PlayPreflight.ViaFor(slot, out _) != LaunchVia.None;

        content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            Text = viaLauncher
                ? "This slot starts through its launcher, which owns the command line. These are read only when Play starts the executable itself."
                : "Used by Play, and only when it starts this game itself.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)FindResource(viaLauncher ? "Amber" : "TextMid"),
            Margin = new Thickness(0, 10, 0, 0),
        });

        ShowCustomModal("LAUNCH OPTIONS FOR " + slot.Name.ToUpperInvariant(), content, "SAVE", () =>
        {
            slot.LaunchArguments = LaunchArguments.Normalise(field.Text);
            Commit();
            BuildSlots();

            Flash(slot.LaunchArguments.Length == 0
                ? "No launch arguments for " + slot.Name
                : "Launch arguments saved for " + slot.Name);
        }, initialFocus: field);
    }


    /// <summary>
    /// The crosshair a slot puts up. Same dropdown as the screen and sound ones,
    /// and it lands on the same handler, which is why its tag has the same shape.
    /// </summary>
    private ComboBox CrosshairCombo(HotkeySlot slot)
    {
        List<PresetChoice> choices = new() { new PresetChoice { Kind = "crosshair", Id = string.Empty, Name = "No crosshair" } };
        foreach (CrosshairPreset preset in AllCrosshairs())
        {
            choices.Add(new PresetChoice { Kind = "crosshair", Id = preset.Id, Name = preset.Name });
        }

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|crosshair",
            ToolTip = "Crosshair"
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Crosshair for " + slot.Name);
        MarqueeBox.SetAllowMarquee(box, true);

        int index = choices.FindIndex(c => string.Equals(c.Id, slot.CrosshairPresetId, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = index < 0 ? 0 : index;
        box.SelectionChanged += OnSlotPresetChanged;
        return box;
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

        // The one place the two halves of a slot disagree. Windows ignores the
        // gamma ramp in HDR, so a screen preset beside HDR On is a preset that
        // will not be visible on that monitor. Said on the control rather than
        // only on the Display tab, because the slot board is where the user made
        // the combination.
        string hdrNote = kind == "display"
            && slot.HdrMode == SlotHdrMode.On
            && !string.IsNullOrWhiteSpace(slot.DisplayPresetId)
                ? "  ·  HDR is on, so this preset's gamma won't apply"
                : string.Empty;

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|" + kind,
            ToolTip = name + hdrNote
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
        else if (parts[1] == "crosshair")
        {
            slot.CrosshairPresetId = choice.Id.Length == 0 ? null : choice.Id;
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
            slot.LaunchUri = string.Empty;
            slot.LaunchConfirmed = false;

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
            slot.LaunchUri = string.Empty;
            slot.LaunchConfirmed = false;

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

                // A file the user picked is started directly. Any launcher link
                // the slot carried pointed at whatever it used to name.
                slot.LaunchUri = string.Empty;
                slot.LaunchConfirmed = true;

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

        // Only what the policy accepts, and only for the game that was just
        // chosen. A candidate from Steam or Epic carries the launcher's own way of
        // starting it; every other candidate carries nothing and the executable is
        // the target.
        slot.LaunchUri = choice.ExePath.Length > 0 && LaunchUriPolicy.IsAllowed(choice.LaunchUri)
            ? choice.LaunchUri
            : string.Empty;

        // Chosen from this PC's own scan, so the path is one this machine found.
        // Cleared rather than left standing when the slot is pointed at no game.
        slot.LaunchConfirmed = choice.ExePath.Length > 0;
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
}
