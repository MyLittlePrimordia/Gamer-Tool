using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    /// Column widths shared by a slot row and the caption strip above it, so the
    /// two can never drift apart. The app dropdown is the only flexible column.
    /// The key column is 112px because "CTRL+SHIFT+5" in 10.5pt mono needs more
    /// than the 84px it used to get and was being cut to "CTRL+SH".
    /// </summary>
    private static readonly GridLength[] SlotColumns =
    {
        new(112), new(10), new(150), new(16), new(196), new(10),
        new(196), new(10), new(112), new(10), new(1, GridUnitType.Star),
        new(16), new(34), new(8), new(30)
    };

    private static void ApplySlotColumns(Grid row)
    {
        foreach (GridLength width in SlotColumns)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
    }


    /// <summary>
    /// Captions for the controls in a slot row. The strip is a child of the same
    /// grid as the rows and carries the card's own 12px padding as a margin, so
    /// each caption lands exactly over the control it names.
    /// </summary>
    private UIElement SlotHeaderRow()
    {
        Grid head = new();
        ApplySlotColumns(head);
        head.Margin = new Thickness(12, 0, 12, 8);

        void Caption(int column, string text, bool right = false)
        {
            TextBlock caption = new()
            {
                Text = text,
                Style = (Style)FindResource("SectionHeader"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            Grid.SetColumn(caption, column);
            head.Children.Add(caption);
        }

        Caption(0, "KEY");
        Caption(2, "SLOT");
        Caption(4, "DISPLAY");
        Caption(6, "SOUND");
        Caption(8, "MONITOR");
        Caption(10, "GAME");
        Caption(12, "AUTO", true);
        Caption(14, "DELETE", true);

        return head;
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
    /// A single slot as one full width row: keycap, name, the three things it
    /// loads, which screen it touches, when it fires, and the two per slot
    /// actions on the far right.
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

        TextBox keyBox = new()
        {
            Style = (Style)FindResource("KeyCap"),
            Tag = slot.Id,
            Height = 28
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
        Grid.SetColumn(keyBox, 0);

        TextBlock nameText = new()
        {
            Text = string.IsNullOrWhiteSpace(slot.Name) ? "Slot" : slot.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = slot.HasWork ? slot.WorkText : "Empty slot",

            // A slot the user added can be named, so the toast that reports a
            // slot loading says something recognisable rather than "SLOT 5".
            Cursor = slot.BuiltIn ? Cursors.Arrow : Cursors.Hand,
            Background = Brushes.Transparent
        };

        if (!slot.BuiltIn)
        {
            nameText.ToolTip = "Rename";
            nameText.MouseLeftButtonUp += (s, e) => RenameSlot(slot);
        }

        Grid.SetColumn(nameText, 2);

        ComboBox displayBox = PresetCombo("display", slot);
        Grid.SetColumn(displayBox, 4);

        ComboBox soundBox = PresetCombo("audio", slot);
        Grid.SetColumn(soundBox, 6);

        ComboBox monitorBox = MonitorCombo(slot);
        Grid.SetColumn(monitorBox, 8);

        ComboBox appBox = AppCombo(slot);
        Grid.SetColumn(appBox, 10);

        bool canAuto = slot.HasWork && (slot.IsSelfTarget || slot.HasTarget);
        GamerTool.UI.SwitchToggle autoBox = new()
        {
            Style = (Style)FindResource("SwitchTrack"),
            Accent = (System.Windows.Media.Brush)FindResource("AccentHotkeys"),
            IsChecked = slot.AutoActivate,
            IsEnabled = slot.AutoActivate || canAuto,
            Tag = slot.Id,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = AutoToolTip(slot)
        };

        System.Windows.Automation.AutomationProperties.SetName(autoBox, "Load this slot automatically");
        autoBox.Checked += (s, e) => SetSlotFlag(slot, "auto", true);
        autoBox.Unchecked += (s, e) => SetSlotFlag(slot, "auto", false);
        Grid.SetColumn(autoBox, 12);

        IconButton remove = new()
        {
            Style = (Style)FindResource("IconButton"),
            Icon = Icons.Trash,
            Tag = slot.Id,
            ToolTip = "Delete Slot",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        remove.Click += (s, e) =>
        {
            _settings.Slots.RemoveAll(x => x.Id == slot.Id);
            Commit();
            BuildSlots();
            RegisterHotkeys();
            ApplyWatchState();
        };
        Grid.SetColumn(remove, 14);

        row.Children.Add(keyBox);
        row.Children.Add(nameText);
        row.Children.Add(displayBox);
        row.Children.Add(soundBox);
        row.Children.Add(monitorBox);
        row.Children.Add(appBox);
        row.Children.Add(autoBox);
        row.Children.Add(remove);

        card.Child = row;
        return card;
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

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|" + kind,
            ToolTip = kind == "display" ? "Screen Preset" : "Sound Preset"
        };

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
    private static void ResolveAppIcons(List<AppCandidate> candidates)
    {
        foreach (AppCandidate candidate in candidates)
        {
            if (candidate.Icon is not null || candidate.ExePath.Length == 0)
            {
                continue;
            }

            if (candidate.ExePath == SelfMarker || candidate.ExePath == BrowseMarker)
            {
                continue;
            }

            candidate.Icon = IconFactory.ExtractAppIcon(candidate.ExePath);
        }
    }


    private ComboBox AppCombo(HotkeySlot slot)
    {
        List<AppCandidate> choices = new()
        {
            new AppCandidate { Name = "No game", ExePath = string.Empty, ProcessName = string.Empty, Source = "NONE" },
            new AppCandidate { Name = "Gamer Tool (on start)", ExePath = SelfMarker, ProcessName = string.Empty, Source = "SELF", Icon = IconFactory.LoadWindowIcon() }
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
            ToolTip = "Auto Switch"
        };

        // Installed programs have long names and this column is narrow, so the
        // closed box scrolls rather than clipping.
        MarqueeBox.SetAllowMarquee(box, true);

        // Icons are pulled the first time the list is actually opened, not on startup.
        box.DropDownOpened += (_, _) => ResolveAppIcons(choices);

        int index = slot.IsSelfTarget ? 1 : 0;
        if (!slot.IsSelfTarget && !string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            index = -1;
            for (int i = 2; i < choices.Count; i++)
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
                choices.Insert(2, manual);
                box.ItemsSource = choices;
                index = 2;
            }
        }

        box.SelectedIndex = index;
        box.SelectionChanged += OnSlotAppChanged;
        return box;
    }


    private const string BrowseMarker = "\\browse";


    private const string SelfMarker = "\\self";


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


    private static bool SameTarget(HotkeySlot a, HotkeySlot b)
    {
        if (a.IsSelfTarget || b.IsSelfTarget)
        {
            return a.IsSelfTarget && b.IsSelfTarget;
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
    }


    private void ReleaseClaimsOn(HotkeySlot slot)
    {
        List<HotkeySlot> others = AutoClaimants(slot);
        foreach (HotkeySlot other in others)
        {
            other.AutoActivate = false;
            Flash("Auto load off for " + other.Name);
        }
    }


    private void SetSlotFlag(HotkeySlot slot, string flag, bool value)
    {
        if (!_ready)
        {
            return;
        }

        if (flag == "auto")
        {
            if (value)
            {
                if (!slot.HasWork)
                {
                    Flash("Nothing to load, pick a screen or sound", true);
                    BuildSlots();
                    return;
                }

                if (!slot.IsSelfTarget && !slot.HasTarget)
                {
                    Flash("Pick a game in the last dropdown", true);
                    BuildSlots();
                    return;
                }

                ReleaseAutoClaims(slot);
            }

            slot.AutoActivate = value;
        }
        else if (flag == "start")
        {
            slot.ApplyOnStart = value;
        }
        else if (flag == "on")
        {
            slot.Enabled = value;
        }

        Commit();
        BuildSlots();
        ApplyWatchState();
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
            ReleaseClaimsOn(slot);
            Commit();
            BuildSlots();
            ApplyWatchState();
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
                slot.AutoActivate = true;
                ReleaseClaimsOn(slot);
                Flash("Target set to " + slot.AppName);
            }

            Commit();
            BuildSlots();
            ApplyWatchState();
            return;
        }

        slot.IsSelfTarget = false;
        slot.ApplyOnStart = false;
        slot.AppExePath = choice.ExePath.Length == 0 ? null : choice.ExePath;
        slot.AppName = choice.ExePath.Length == 0 ? null : choice.Name;
        if (slot.AppExePath is not null)
        {
            slot.AutoActivate = true;
            ReleaseClaimsOn(slot);
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


    private async System.Threading.Tasks.Task EnsureAppList()
    {
        if (_appList.Count > 0)
        {
            return;
        }

        _appList = (await System.Threading.Tasks.Task.Run(() => _library.Scan())).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }


    private async void OnScanAppsClick(object sender, RoutedEventArgs e)
    {
        RailStatus.Text = "SCANNING FOR GAMES";
        await EnsureAppList();
        BuildSlots();
        RailStatus.Text = "READY";
        Flash(_appList.Count.ToString(CultureInfo.InvariantCulture) + " games found");
    }


    private void PlaySlot(HotkeySlot slot, bool announce)
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

        if (display is not null)
        {
            LoadTune(display.Copy(), (audio ?? _workAudio).Copy());
            ApplyDisplay(display.Copy(), slot.MonitorDevice, false);
        }

        if (audio is not null)
        {
            ApplyAudio(audio.Copy(), false);
        }

        RailStatus.Text = slot.Name.ToUpperInvariant();
        if (announce)
        {
            Flash(slot.Name + " loaded");
        }
    }


    private void OnForegroundChanged(WatchedWindow window)
    {
        if (!_settings.AutoSwitch)
        {
            return;
        }

        HotkeySlot? slot = SlotService.MatchForeground(_settings.Slots, window.ExePath, window.ProcessName);
        if (slot is not null && ShouldAutoApply(slot, window.ProcessName))
        {
            TraceLog.Write("AUTO FOCUS " + slot.Name + " <- " + window.ExePath);
            PlaySlot(slot, true);
        }
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
        if (!_settings.AutoRevertOnExit || _quitting)
        {
            return;
        }

        HotkeySlot? slot = SlotService.MatchProcessName(_settings.Slots, processName);
        if (slot is null)
        {
            return;
        }

        if (!string.Equals(_autoSlotId, slot.Id, StringComparison.OrdinalIgnoreCase))
        {
            TraceLog.Write("AUTO EXIT SKIP " + processName + " <- not the auto loaded slot");
            return;
        }

        TraceLog.Write("AUTO EXIT " + slot.Name + " <- " + processName);

        // GoScreenNeutral stands the auto-apply guard down as it goes, so a second
        // exit for the same slot cannot fire a second revert.
        GoScreenNeutral();

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

        if (DateTime.UtcNow - _autoStamp < TimeSpan.FromSeconds(2))
        {
            return false;
        }

        _autoStamp = DateTime.UtcNow;
        _autoSlotId = slot.Id;
        _autoProcess = processName ?? string.Empty;
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
            if (taken.Add(panicKey))
            {
                _hotkeys.Register(EmergencyHotkeyId, EmergencyTargetId, _settings.EmergencyHotkey);
            }
        }

        foreach (HotkeySlot slot in _settings.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.Hotkey) && slot.Enabled)
            {
                string key = HotkeyService.Normalise(slot.Hotkey);
                if (taken.Add(key))
                {
                    _hotkeys.Register(id, "slot:" + slot.Id, slot.Hotkey);
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

            RailStatus.Text = "DUP KEY SKIPPED ON " + names.ToUpperInvariant();
            Flash(
                lostToPanic
                    ? "Panic key took " + keys + " from " + names
                    : "Key already on another slot, skipped on " + names,
                true);
            TraceLog.Write("HOTKEY duplicate " + keys + " skipped on " + names);
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
        if (string.Equals(binding.TargetId, EmergencyTargetId, StringComparison.OrdinalIgnoreCase))
        {
            // The same neutral path a slot takes when it is switched off, so the
            // panic key lands in exactly the state quitting would have left, and
            // there is only one definition of "back to normal" in the app.
            GoScreenNeutral();
            await GoSoundNeutralAsync();
            RailStatus.Text = "PANIC RESET";
            Flash("Screen and sound reset");
            TraceLog.Write("PANIC key pressed, screen and sound reset");
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

            if (!slot.HasWork)
            {
                RailStatus.Text = "NOTHING SET";
                Flash("Nothing set on " + slot.Name, true);
                return;
            }

            if (SlotService.IsLoaded(slot, _activeDisplayId, _activeAudioId))
            {
                GoScreenNeutral();
                await GoSoundNeutralAsync();
                Flash(slot.Name + " off");
                return;
            }

            PlaySlot(slot, true);
        }
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
    /// Points the panic key at a new combo, taking it off any slot that held it.
    /// The steal is the same rule the slots use, the thing you are pointing at
    /// wins, because a slot quietly keeping a chord the user has just given away
    /// is how you end up with two things bound to one key and no way to tell.
    /// </summary>
    private void BindPanicHotkey(string text)
    {
        string wanted = HotkeyService.Normalise(text);
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


    /// <summary>
    /// Gives the panic keycap the same behaviour as a slot keycap: click to
    /// listen, Esc cancels, Backspace clears, never free text. Wired once here
    /// rather than rebuilt with the rest of the page, because the box is declared
    /// in the page and lives as long as the window does.
    /// </summary>
    private void WirePanicKeycap()
    {
        ShowSlotKey(PanicKeyBox, _settings.EmergencyHotkey);

        PanicKeyBox.GotKeyboardFocus += (s, e) =>
        {
            _capturingPanic = true;
            _captureSlotId = null;
            _captureBox = PanicKeyBox;
            PanicKeyBox.Text = "PRESS A KEY";
            PanicKeyBox.ToolTip = "Esc cancels  ·  Backspace clears  ·  F1 to F24 bind on their own";
        };
        PanicKeyBox.LostKeyboardFocus += (s, e) =>
        {
            if (!ReferenceEquals(_captureBox, PanicKeyBox))
            {
                return;
            }

            ShowSlotKey(PanicKeyBox, _settings.EmergencyHotkey);
            _captureBox = null;
            _capturingPanic = false;
        };
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
