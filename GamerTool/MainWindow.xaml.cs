using System.Windows;
using System.Windows.Controls;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;

namespace GamerTool;

public partial class MainWindow : Window
{
    private AppSettings _settings = null!;
    private readonly HotkeyService _hotkeyService = new();
    private FocusWatcher? _focusWatcher;
    private readonly List<Slider> _bandSliders = new();

    private DisplayPreset _currentDisplay = DisplayPreset.Daylight;
    private AudioPreset _currentAudio = AudioPreset.Flat;

    private bool _suppressEvents; // guards against feedback loops while populating controls

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = ProfileManager.Load();

        BuildBandSliders();
        RefreshPresetLists();
        RefreshOutputDevices();
        RefreshEngineStatus();

        // Attach event handlers after controls are fully initialized
        BrightnessSlider.ValueChanged += DisplaySlider_ValueChanged;
        ContrastSlider.ValueChanged += DisplaySlider_ValueChanged;
        GammaSlider.ValueChanged += DisplaySlider_ValueChanged;
        ShadowBoostSlider.ValueChanged += DisplaySlider_ValueChanged;
        RedSlider.ValueChanged += DisplaySlider_ValueChanged;
        GreenSlider.ValueChanged += DisplaySlider_ValueChanged;
        BlueSlider.ValueChanged += DisplaySlider_ValueChanged;
        PreampSlider.ValueChanged += AudioControl_Changed;
        AntiClipCheck.Checked += AudioControl_Changed;
        AntiClipCheck.Unchecked += AudioControl_Changed;

        // Restore last-applied state
        _currentDisplay = _settings.DisplayPresets.FirstOrDefault(p => p.Name == _settings.LastDisplayPreset)
                          ?? DisplayPreset.Daylight;
        _currentAudio = _settings.AudioPresets.FirstOrDefault(p => p.Name == _settings.LastAudioPreset)
                        ?? AudioPreset.Flat;

        LoadDisplayIntoControls(_currentDisplay);
        LoadAudioIntoControls(_currentAudio);
        ApplyCurrentDisplay();
        // Audio isn't force-applied at startup unless the engine is already ready,
        // so we never trip the "engine not set up" exception on a fresh install.
        if (AudioService.IsEngineReady())
            TryApplyCurrentAudio();

        _focusWatcher = new FocusWatcher();
        _focusWatcher.Start();

        _hotkeyService.AttachToWindow(this);
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        ReloadHotkeys();

        RefreshHotkeyGrid();
    }

    // ============================= DISPLAY TAB =============================

    private void LoadDisplayIntoControls(DisplayPreset p)
    {
        _suppressEvents = true;
        BrightnessSlider.Value = p.Brightness;
        ContrastSlider.Value = p.Contrast;
        GammaSlider.Value = p.Gamma;
        ShadowBoostSlider.Value = p.ShadowBoost;
        RedSlider.Value = p.RedScale;
        GreenSlider.Value = p.GreenScale;
        BlueSlider.Value = p.BlueScale;
        _suppressEvents = false;
    }

    private DisplayPreset ReadDisplayFromControls(string name) => new()
    {
        Name = name,
        Brightness = BrightnessSlider.Value,
        Contrast = ContrastSlider.Value,
        Gamma = GammaSlider.Value,
        ShadowBoost = ShadowBoostSlider.Value,
        RedScale = RedSlider.Value,
        GreenScale = GreenSlider.Value,
        BlueScale = BlueSlider.Value
    };

    private void DisplaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        _currentDisplay = ReadDisplayFromControls(_currentDisplay.Name);
        ApplyCurrentDisplay();
    }

    private void ApplyCurrentDisplay()
    {
        try
        {
            DisplayService.Apply(_currentDisplay);
            _settings.LastDisplayPreset = _currentDisplay.Name;
            ProfileManager.Save(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Display", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetDisplay_Click(object sender, RoutedEventArgs e) => DisplayService.ResetToIdentity();

    private void SaveDisplayPreset_Click(object sender, RoutedEventArgs e)
    {
        string? name = InputDialog.Show(this, "Name this display preset:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = ReadDisplayFromControls(name);
        _settings.DisplayPresets.RemoveAll(p => p.Name == name);
        _settings.DisplayPresets.Add(preset);
        _currentDisplay = preset;
        ProfileManager.Save(_settings);
        RefreshPresetLists();
    }

    private void DeleteDisplayPreset_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPresetList.SelectedItem is not DisplayPreset preset) return;
        _settings.DisplayPresets.Remove(preset);
        ProfileManager.Save(_settings);
        RefreshPresetLists();
    }

    private void DisplayPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayPresetList.SelectedItem is not DisplayPreset preset) return;
        _currentDisplay = preset;
        LoadDisplayIntoControls(preset);
        ApplyCurrentDisplay();
    }

    // ============================== AUDIO TAB ===============================

    private void BuildBandSliders()
    {
        BandSlidersHost.Items.Clear();
        _bandSliders.Clear();

        foreach (int freq in AudioPreset.Frequencies)
        {
            var stack = new StackPanel { Margin = new Thickness(6, 0, 6, 0), Width = 48 };

            var slider = new Slider
            {
                Style = (Style)FindResource("EqBandSlider"),
                Tag = freq
            };
            slider.ValueChanged += AudioControl_Changed;
            _bandSliders.Add(slider);

            var freqLabel = new TextBlock
            {
                Text = freq >= 1000 ? $"{freq / 1000}k" : freq.ToString(),
                Foreground = System.Windows.Media.Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            };

            stack.Children.Add(slider);
            stack.Children.Add(freqLabel);
            BandSlidersHost.Items.Add(stack);
        }
    }

    private void LoadAudioIntoControls(AudioPreset p)
    {
        _suppressEvents = true;
        for (int i = 0; i < _bandSliders.Count && i < p.Bands.Length; i++)
            _bandSliders[i].Value = p.Bands[i];
        PreampSlider.Value = p.Preamp;
        AntiClipCheck.IsChecked = p.AntiClip;
        _suppressEvents = false;
    }

    private AudioPreset ReadAudioFromControls(string name) => new()
    {
        Name = name,
        Bands = _bandSliders.Select(s => s.Value).ToArray(),
        Preamp = PreampSlider.Value,
        AntiClip = AntiClipCheck.IsChecked == true
    };

    private void AudioControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _currentAudio = ReadAudioFromControls(_currentAudio.Name);
        TryApplyCurrentAudio();
    }

    private void TryApplyCurrentAudio()
    {
        try
        {
            AudioService.ApplyPreset(_currentAudio);
            _settings.LastAudioPreset = _currentAudio.Name;
            ProfileManager.Save(_settings);
        }
        catch (InvalidOperationException)
        {
            // Engine not ready yet; the banner already tells the user what to do.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveAudioPreset_Click(object sender, RoutedEventArgs e)
    {
        string? name = InputDialog.Show(this, "Name this audio preset:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = ReadAudioFromControls(name);
        _settings.AudioPresets.RemoveAll(p => p.Name == name);
        _settings.AudioPresets.Add(preset);
        _currentAudio = preset;
        ProfileManager.Save(_settings);
        RefreshPresetLists();
    }

    private void DeleteAudioPreset_Click(object sender, RoutedEventArgs e)
    {
        if (AudioPresetList.SelectedItem is not AudioPreset preset) return;
        _settings.AudioPresets.Remove(preset);
        ProfileManager.Save(_settings);
        RefreshPresetLists();
    }

    private void AudioPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioPresetList.SelectedItem is not AudioPreset preset) return;
        _currentAudio = preset;
        LoadAudioIntoControls(preset);
        TryApplyCurrentAudio();
    }

    private void EnableAudioEq_Click(object sender, RoutedEventArgs e)
    {
        EngineStatusText.Text = "Audio Engine: setting up (check for a UAC prompt)...";

        var outcome = AudioService.RunElevatedSetup();
        RefreshEngineStatus();

        string message = outcome switch
        {
            EqualizerApoInstallerService.SetupOutcome.Success =>
                "Audio EQ is ready — Equalizer APO was installed and registered for your default playback device.",
            EqualizerApoInstallerService.SetupOutcome.AlreadyInstalled =>
                "Audio EQ was already set up. You're good to go.",
            EqualizerApoInstallerService.SetupOutcome.DownloadFailed =>
                "Couldn't download Equalizer APO (check your internet connection) and no existing install was " +
                "found. You can also install it yourself from sourceforge.net/projects/equalizerapo and click " +
                "\"Enable Audio EQ\" again.",
            EqualizerApoInstallerService.SetupOutcome.InstallFailed =>
                "The Equalizer APO installer ran but setup couldn't confirm it installed correctly. Try running " +
                "it manually, or check C:\\ProgramData\\GamerTool\\EQ\\setup.log for details.",
            EqualizerApoInstallerService.SetupOutcome.DeviceAutoRegisterFailed_ConfiguratorOpened =>
                "Equalizer APO is installed, but GamerTool couldn't automatically wire it to your playback " +
                "device — Equalizer APO's own Configurator window should have opened. Just tick the box next " +
                "to your device there (Install APO as SFX/EFX) and click OK.",
            EqualizerApoInstallerService.SetupOutcome.DeviceAlreadyHasOtherEffects_ConfiguratorOpened =>
                "Your default playback device already has another audio enhancement tool configured, so " +
                "GamerTool didn't override it. Equalizer APO's Configurator opened so you can decide how to " +
                "combine or replace it.",
            null =>
                "Setup needs admin approval to install/configure Equalizer APO and grant the Windows audio " +
                "service access to the EQ config folder. Without it, audio EQ can't run reliably.",
            _ => "Setup finished with an unexpected result."
        };

        MessageBox.Show(this, message, "Audio Engine", MessageBoxButton.OK,
            outcome == EqualizerApoInstallerService.SetupOutcome.Success ||
            outcome == EqualizerApoInstallerService.SetupOutcome.AlreadyInstalled
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);

        if (outcome is EqualizerApoInstallerService.SetupOutcome.Success or
            EqualizerApoInstallerService.SetupOutcome.AlreadyInstalled)
        {
            TryApplyCurrentAudio();
        }
    }

    private void RefreshEngineStatus()
    {
        bool ready = AudioService.IsEngineReady();
        EngineStatusText.Text = ready ? "Audio Engine: Ready" : "Audio Engine: Setup Required";
        EnableAudioBanner.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============================ OUTPUT DEVICE =============================

    private void RefreshOutputDevices()
    {
        var devices = AudioDeviceService.EnumerateRenderDevices();
        OutputDeviceCombo.Items.Clear();
        OutputDeviceCombo.Items.Add("System Default");

        foreach (var d in devices)
            OutputDeviceCombo.Items.Add(d.FriendlyName);

        OutputDeviceCombo.SelectedIndex = 0;
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Equalizer APO applies per-endpoint; full device-specific re-registration
        // is out of scope here since it requires re-running the elevated installer
        // targeted at a specific device GUID. We persist the friendly name choice
        // so a future setup pass can use it.
        if (OutputDeviceCombo.SelectedItem is string name)
        {
            _settings.OutputDeviceId = name;
            ProfileManager.Save(_settings);
        }
    }

    // ============================== HOTKEYS TAB ==============================

    private void RefreshPresetLists()
    {
        DisplayPresetList.ItemsSource = null;
        DisplayPresetList.ItemsSource = _settings.DisplayPresets;
        DisplayPresetList.DisplayMemberPath = nameof(DisplayPreset.Name);

        AudioPresetList.ItemsSource = null;
        AudioPresetList.ItemsSource = _settings.AudioPresets;
        AudioPresetList.DisplayMemberPath = nameof(AudioPreset.Name);
    }

    private void RefreshHotkeyGrid()
    {
        HotkeyGrid.ItemsSource = null;
        HotkeyGrid.ItemsSource = _settings.Hotkeys;
    }

    private void AddHotkey_Click(object sender, RoutedEventArgs e)
    {
        var capture = new HotkeyCaptureWindow { Owner = this };
        if (capture.ShowDialog() != true) return;

        string? target = InputDialog.Show(this,
            "Target preset/combo name (must match an existing Display, Audio, or Combo preset name exactly):");
        if (string.IsNullOrWhiteSpace(target)) return;

        var action = ResolveActionForTarget(target);

        var binding = new HotkeyBinding
        {
            Modifiers = capture.CapturedModifiers,
            Key = capture.CapturedVirtualKey,
            DisplayText = capture.CapturedLabel,
            TargetName = target,
            Action = action
        };

        _settings.Hotkeys.Add(binding);
        ProfileManager.Save(_settings);
        RefreshHotkeyGrid();
        ReloadHotkeys();
    }

    private BindingAction ResolveActionForTarget(string target)
    {
        if (_settings.ComboPresets.Any(c => c.Name == target)) return BindingAction.Combo;
        if (_settings.AudioPresets.Any(a => a.Name == target)) return BindingAction.AudioOnly;
        return BindingAction.DisplayOnly;
    }

    private void RemoveHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HotkeyBinding binding }) return;
        _settings.Hotkeys.Remove(binding);
        ProfileManager.Save(_settings);
        RefreshHotkeyGrid();
        ReloadHotkeys();
    }

    private void HotkeyGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProfileManager.Save(_settings);
            ReloadHotkeys();
        });
    }

    private void AddCombo_Click(object sender, RoutedEventArgs e)
    {
        string? name = InputDialog.Show(this, "Combo preset name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var combo = new ComboPreset
        {
            Name = name,
            DisplayPresetName = _currentDisplay.Name,
            AudioPresetName = _currentAudio.Name
        };
        _settings.ComboPresets.RemoveAll(c => c.Name == name);
        _settings.ComboPresets.Add(combo);
        ProfileManager.Save(_settings);

        MessageBox.Show(this,
            $"Combo \"{name}\" saved: {combo.DisplayPresetName} + {combo.AudioPresetName}.\n" +
            "Bind it to a hotkey from \"+ Add Hotkey Binding\" using this exact name.",
            "Combo Preset", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReloadHotkeys()
    {
        _hotkeyService.ReloadAll(_settings.Hotkeys, out var failed);
        if (failed.Count > 0)
        {
            MessageBox.Show(this,
                $"{failed.Count} hotkey(s) couldn't be registered (likely already claimed by another app):\n" +
                string.Join("\n", failed.Select(f => $"{f.DisplayText} -> {f.TargetName}")),
                "Hotkeys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnHotkeyPressed(HotkeyBinding binding)
    {
        switch (binding.Action)
        {
            case BindingAction.DisplayOnly:
                ApplyDisplayByName(binding.TargetName);
                ShowOsd($"{binding.TargetName}");
                break;

            case BindingAction.AudioOnly:
                ApplyAudioByName(binding.TargetName);
                ShowOsd($"{binding.TargetName}");
                break;

            case BindingAction.Combo:
                var combo = _settings.ComboPresets.FirstOrDefault(c => c.Name == binding.TargetName);
                if (combo != null)
                {
                    ApplyDisplayByName(combo.DisplayPresetName);
                    ApplyAudioByName(combo.AudioPresetName);
                    ShowOsd($"{combo.DisplayPresetName} + {combo.AudioPresetName}");
                }
                break;

            case BindingAction.PanicReset:
                AudioService.PanicReset();
                ShowOsd("Audio Reset");
                break;
        }
    }

    private void ApplyDisplayByName(string name)
    {
        var preset = _settings.DisplayPresets.FirstOrDefault(p => p.Name == name);
        if (preset == null) return;
        _currentDisplay = preset;
        Dispatcher.Invoke(() => LoadDisplayIntoControls(preset));
        ApplyCurrentDisplay();
    }

    private void ApplyAudioByName(string name)
    {
        var preset = _settings.AudioPresets.FirstOrDefault(p => p.Name == name);
        if (preset == null) return;
        _currentAudio = preset;
        Dispatcher.Invoke(() => LoadAudioIntoControls(preset));
        TryApplyCurrentAudio();
    }

    private void ShowOsd(string message)
    {
        if (_settings.ShowOsdToast)
            OsdNotification.ShowToast($"GamerTool: {message} active");
    }

    // ============================== PANIC BUTTON =============================

    private void PanicButton_Click(object sender, RoutedEventArgs e)
    {
        AudioService.PanicReset();
        ShowOsd("Audio Reset");
        RefreshEngineStatus();
    }

    // ============================== SHUTDOWN =============================

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ProfileManager.Save(_settings);
        _focusWatcher?.Dispose();
        _hotkeyService.Dispose();
        // Always restore the original Windows gamma ramp on exit so the
        // user is never left with a permanent tint / brightness change.
        DisplayService.ResetToIdentity();
    }
}
