using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

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


    private void BuildSlots()
    {
        SlotList.Children.Clear();
        _slotRows.Clear();

        foreach (HotkeySlot slot in _settings.Slots)
        {
            SlotList.Children.Add(SlotRow(slot));
        }

        if (_settings.Slots.Count == 0)
        {
            SlotHint.Text = "No slots yet. Hit Add slot to make one.";
            return;
        }

        SlotHint.Text = "None on a side means that half is skipped. The toggle only works while auto load is on in Settings.";
    }


    private UIElement SlotRow(HotkeySlot slot)
    {
        Border row = new()
        {
            Style = (Style)FindResource("Row"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        _slotRows[slot.Id] = row;

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        StackPanel nameBox = new()
        {
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock nameText = new()
        {
            Text = string.IsNullOrWhiteSpace(slot.Name) ? "SLOT" : slot.Name.ToUpperInvariant(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        TextBlock nameSub = new()
        {
            Text = slot.WorkText,
            Style = (Style)FindResource("CardSub"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        nameBox.Children.Add(nameText);
        nameBox.Children.Add(nameSub);
        Grid.SetColumn(nameBox, 0);

        TextBlock plus = new()
        {
            Text = "+",
            FontSize = 16,
            Foreground = (Brush)FindResource("TextLow"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(plus, 2);

        ComboBox displayBox = PresetCombo("display", slot);
        Grid.SetColumn(displayBox, 1);

        ComboBox soundBox = PresetCombo("audio", slot);
        Grid.SetColumn(soundBox, 3);

        TextBox keyBox = new()
        {
            Style = (Style)FindResource("ModernTextBox"),
            Text = slot.Hotkey,
            Tag = slot.Id,
            FontFamily = (FontFamily)FindResource("Mono"),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            ToolTip = "Click, then press the new key combo"
        };
        keyBox.GotKeyboardFocus += (s, e) =>
        {
            _captureSlotId = slot.Id;
            _captureBox = keyBox;
            keyBox.SelectAll();
        };
        keyBox.LostKeyboardFocus += (s, e) =>
        {
            if (ReferenceEquals(_captureBox, keyBox))
            {
                _captureSlotId = null;
                _captureBox = null;
            }
        };
        Grid.SetColumn(keyBox, 4);

        ComboBox monitorBox = MonitorCombo(slot);
        Grid.SetColumn(monitorBox, 6);

        ComboBox appBox = AppCombo(slot);
        Grid.SetColumn(appBox, 8);

        bool canAuto = slot.HasWork && (slot.IsSelfTarget || slot.HasTarget);
        CheckBox autoBox = new()
        {
            Style = (Style)FindResource("ModernToggle"),
            IsChecked = slot.AutoActivate,
            IsEnabled = slot.AutoActivate || canAuto,
            Tag = slot.Id,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = AutoToolTip(slot)
        };
        autoBox.Checked += (s, e) => SetSlotFlag(slot, "auto", true);
        autoBox.Unchecked += (s, e) => SetSlotFlag(slot, "auto", false);
        Grid.SetColumn(autoBox, 10);

        Button remove = new()
        {
            Content = "x",
            Style = (Style)FindResource("IconButton"),
            Foreground = (Brush)FindResource("TextMid"),
            Tag = slot.Id,
            ToolTip = "Remove slot",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        remove.Click += (s, e) =>
        {
            _settings.Slots.RemoveAll(x => x.Id == slot.Id);
            Commit();
            BuildSlots();
            RegisterHotkeys();
            ApplyWatchState();
        };
        Grid.SetColumn(remove, 11);

        grid.Children.Add(nameBox);
        grid.Children.Add(displayBox);
        grid.Children.Add(plus);
        grid.Children.Add(soundBox);
        grid.Children.Add(keyBox);
        grid.Children.Add(monitorBox);
        grid.Children.Add(appBox);
        grid.Children.Add(autoBox);
        grid.Children.Add(remove);
        row.Child = grid;
        return row;
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
            ToolTip = "Which screen this slot changes",
            Margin = new Thickness(0, 0, 0, 0)
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
        List<PresetChoice> choices = new() { new PresetChoice { Kind = kind, Id = string.Empty, Name = "None" } };

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
        }

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemsSource = choices,
            Tag = slot.Id + "|" + kind,
            Margin = new Thickness(0, 0, 0, 0)
        };

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
            new AppCandidate { Name = "None", ExePath = string.Empty, ProcessName = string.Empty, Source = "NONE" },
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
            ToolTip = "Pick when this slot loads: a game, a file, or Gamer Tool on start",
            Margin = new Thickness(0, 0, 0, 0)
        };

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
                    Name = string.IsNullOrWhiteSpace(slot.AppName) ? "PICKED" : slot.AppName,
                    ExePath = slot.AppExePath,
                    ProcessName = AppProfileTools.ProcessNameOf(slot.AppExePath),
                    Source = "MANUAL"
                };
                manual.Icon = IconFactory.ExtractAppIcon(manual.ExePath);
                choices.Insert(2, manual);                box.ItemsSource = choices;
                index = 2;
            }
        }

        box.SelectedIndex = index;
        box.SelectionChanged += OnSlotAppChanged;
        return box;
    }


    private const string BrowseMarker = "\\browse";


    private const string SelfMarker = "\\self";


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
            return "Pick a screen or a sound first";
        }

        if (slot.IsSelfTarget)
        {
            return "Loads when Gamer Tool starts";
        }

        if (!slot.HasTarget)
        {
            return "Pick a game in LOAD WHEN first";
        }

        return "Load this slot when " + slot.TargetText + " runs";
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
            Flash("[ AUTO MOVED ]", "OFF FOR " + other.Name.ToUpperInvariant());
        }

        if (AutoClaimants(slot).Count > 0)
        {
            Commit();
            BuildSlots();
            ApplyWatchState();
        }
    }


    private void ReleaseClaimsOn(HotkeySlot slot)
    {
        List<HotkeySlot> others = AutoClaimants(slot);
        foreach (HotkeySlot other in others)
        {
            other.AutoActivate = false;
            Flash("[ AUTO MOVED ]", "OFF FOR " + other.Name.ToUpperInvariant());
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
                    Flash("[ NOTHING TO LOAD ]", "PICK A SCREEN OR A SOUND");
                    BuildSlots();
                    return;
                }

                if (!slot.IsSelfTarget && !slot.HasTarget)
                {
                    Flash("[ NO TARGET ]", "PICK A GAME IN LOAD WHEN");
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
            Flash("[ SLOT EMPTY ]", "AUTO TURNED OFF");
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
                Title = "PICK THE GAME OR APP FILE",
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
                Flash("[ TARGET SET ]", slot.AppName.ToUpperInvariant());
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
            string? folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string shell = Environment.GetFolderPath(Environment.SpecialFolder.System);
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
        await EnsureAppList();
        BuildSlots();
        Flash("[ GAMES SCANNED ]", _appList.Count.ToString(CultureInfo.InvariantCulture) + " FOUND");
    }


    private void PlaySlot(HotkeySlot slot, bool announce)
    {
        DisplayPreset? display = FindDisplay(slot.DisplayPresetId);
        AudioPreset? audio = FindAudio(slot.AudioPresetId);

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
            Flash("[ " + slot.Name.ToUpperInvariant() + " ]", slot.WorkText);
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


    private void RegisterHotkeys()
    {
        _hotkeys.Clear();
        int id = 1;
        foreach (HotkeySlot slot in _settings.Slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.Hotkey) && slot.Enabled)
            {
                _hotkeys.Register(id, "slot:" + slot.Id, slot.Hotkey);
            }

            id++;
        }
    }


    private void OnHotkeyPressed(HotkeyBinding binding)
    {
        if (binding.TargetId.StartsWith("slot:", StringComparison.OrdinalIgnoreCase))
        {
            string id = binding.TargetId["slot:".Length..];
            HotkeySlot? slot = _settings.Slots.FirstOrDefault(s => s.Id == id);
            if (slot is not null)
            {
                PlaySlot(slot, true);
            }
        }
    }


    private void OnHotkeyFailed(string text)
    {
        RailStatus.Text = "KEY BUSY " + text;
    }


    private void OnResetSlotsClick(object sender, RoutedEventArgs e)
    {
        ShowConfirmModal(
            "RESET ALL SLOTS?",
            "This deletes every custom slot, app target and hotkey you have set and puts the built-in defaults back. This cannot be undone.",
            "Reset slots",
            () =>
            {
                _settings.Slots = SlotService.DefaultSlots();
                Commit();
                BuildSlots();
                RegisterHotkeys();
                ApplyWatchState();
                Flash("[ SLOTS RESET ]", "BACK TO DEFAULTS");
            });
    }


}
