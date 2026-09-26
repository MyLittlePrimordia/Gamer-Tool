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
    private void LoadDevices()
    {
        _suppressDeviceEvents = true;
        try
        {
            List<DeviceChoice> choices = new() { new DeviceChoice { Id = string.Empty, Name = "System default" } };
            IReadOnlyList<string> fxDevices = _audio.IsInstalled ? _audio.GetOutputDevices() : Array.Empty<string>();
            if (fxDevices.Count > 0)
            {
                foreach (string name in fxDevices)
                {
                    choices.Add(new DeviceChoice { Id = name, Name = name });
                }
            }
            else
            {
                foreach (AudioDeviceInfo device in _devices.GetOutputDevices())
                {
                    choices.Add(new DeviceChoice { Id = device.Id, Name = device.Name });
                }
            }

            DeviceBox.ItemsSource = choices;
            SetupDeviceBox.ItemsSource = choices;
            int index = choices.FindIndex(c => string.Equals(c.Id, _settings.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
            DeviceBox.SelectedIndex = index < 0 ? 0 : index;
            SetupDeviceBox.SelectedIndex = index < 0 ? 0 : index;
        }
        finally
        {
            _suppressDeviceEvents = false;
        }
    }


    /// <summary>
    /// FxSound follows the Windows default playback endpoint. When that default is
    /// monitor/HDMI audio there are no speakers, so every preset sounds muted. If
    /// there is exactly one real playback endpoint we route to it automatically;
    /// with several we warn instead, because guessing would send audio to the
    /// wrong speakers.
    /// </summary>
    private void EnsureUsableAudioOutput()
    {
        if (_settings.OutputDeviceId.Length > 0)
        {
            return;
        }

        string selected = FxSoundState.TryRead()?.SelectedOutput ?? string.Empty;
        string defaultOutput = _audioDevices.DefaultOutputName();
        if (selected.Length == 0 || !AudioDeviceService.LooksLikeDisplayOutput(selected))
        {
            return;
        }

        if (defaultOutput.Length == 0 || !AudioDeviceService.LooksLikeDisplayOutput(defaultOutput))
        {
            return;
        }

        List<string> known = _audio.IsInstalled
            ? _audio.GetOutputDevices().ToList()
            : _devices.GetOutputDevices().Select(d => d.Name).ToList();

        List<string> real = _audioDevices.ListOutputs()
            .Where(endpoint => !endpoint.LooksLikeDisplay)
            .Where(endpoint => known.Any(k => k.Contains(endpoint.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(endpoint => endpoint.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (real.Count == 0)
        {
            _osd.ShowToast("[ AUDIO OUTPUT PROBLEM ]", "NO SPEAKERS FOUND, PICK AN OUTPUT BELOW");
            return;
        }

        if (real.Count > 1)
        {
            _osd.ShowToast("[ AUDIO OUTPUT PROBLEM ]", "PICK YOUR SPEAKERS IN THE OUTPUT BOX");
            return;
        }

        _settings.OutputDeviceId = real[0];
        _profiles.Save(_settings);
        LoadDevices();
        _osd.ShowToast("[ AUDIO OUTPUT FIXED ]", "NOW USING " + real[0].ToUpperInvariant());
    }


    private string SelectedDeviceName()
    {
        return DeviceBox.SelectedItem is DeviceChoice choice ? choice.Id : string.Empty;
    }


    private void UpdateFxBanner()
    {
        bool missing = !_setup.IsInstalled(_audio);
        if (missing)
        {
            FxPathText.Text = "FXSOUND NOT FOUND";
            FxStateText.Text = "NOT INSTALLED";
            FxStateText.Foreground = (Brush)FindResource("Amber");
        }
        else
        {
            FxPathText.Text = _audio.ExePath;
            FxStateText.Text = "READY";
            FxStateText.Foreground = (Brush)FindResource("Green");
        }
    }


    private void BuildAudioCards()
    {
        AudioPresetGrid.Children.Clear();
        _audioCards.Clear();

        foreach (AudioPreset preset in AllAudioPresets())
        {
            bool isMine = _settings.CustomAudioPresets.Any(p => p.Id == preset.Id);
            AudioPresetGrid.Children.Add(PresetCard(preset, isMine, _audioCards, OnAudioCardClick, OnAudioCardDelete, OnAudioCardRename));
        }

        HighlightCards();
    }


    private void BuildBandStrip(int count)
    {
        BandGrid.Children.Clear();
        _bandSliders.Clear();
        _bandValueBlocks.Clear();
        _bandLabelBlocks.Clear();
        BandGrid.Columns = count <= 5 ? count : (count <= 10 ? 10 : 16);

        for (int i = 0; i < count; i++)
        {
            Grid cell = new();
            cell.Margin = new Thickness(3, 0, 3, 0);

            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock value = new()
            {
                Style = (Style)FindResource("Value"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = "0.0",
                Margin = new Thickness(0, 0, 0, 4)
            };

            Slider slider = new()
            {
                Style = (Style)FindResource("BandSliderVertical"),
                Minimum = AudioPreset.GainMin,
                Maximum = AudioPreset.GainMax,
                Height = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0)
            };
            slider.ValueChanged += OnBandChanged;

            TextBlock label = new()
            {
                Style = (Style)FindResource("Label"),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = (FontFamily)FindResource("Mono"),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0),
                Text = AudioPreset.FormatFrequency(i < AudioPreset.DefaultBandFrequencies.Length ? AudioPreset.DefaultBandFrequencies[i] : 1000.0)
            };

            Grid.SetRow(value, 0);
            Grid.SetRow(slider, 1);
            Grid.SetRow(label, 2);

            cell.Children.Add(value);
            cell.Children.Add(slider);
            cell.Children.Add(label);
            BandGrid.Children.Add(cell);

            _bandSliders.Add(slider);
            _bandValueBlocks.Add(value);
            _bandLabelBlocks.Add(label);
        }
    }


    private static void EnsureBands(AudioPreset preset, int count)
    {
        if (preset.Bands.Length >= count)
        {
            return;
        }

        double[] grown = new double[count];
        for (int i = 0; i < count; i++)
        {
            grown[i] = i < preset.Bands.Length ? preset.Bands[i] : 0.0;
        }

        preset.Bands = grown;
    }


    private void UpdateSoundLabels(AudioPreset preset)
    {
        _workAudio = preset;
        ClarityValue.Text = preset.ClarityText;
        AmbienceValue.Text = preset.AmbienceText;
        SurroundValue.Text = preset.SurroundText;
        DynamicBoostValue.Text = preset.DynamicBoostText;
        BassBoostValue.Text = preset.BassBoostText;
        MasterGainValue.Text = Signed(preset.MasterGain, "0.0");
        LevelingValue.Text = preset.VolumeLeveling.ToString("0.0", CultureInfo.InvariantCulture);
        FilterQValue.Text = preset.FilterQ.ToString("0.0", CultureInfo.InvariantCulture);
        BalanceValue.Text = Signed(preset.Balance, "0.0");
        for (int i = 0; i < _bandValueBlocks.Count; i++)
        {
            _bandValueBlocks[i].Text = Signed(preset.Band(i), "0.0");
        }

        // Keep the local preview mix in step with the staged preset, otherwise
        // picking a preset leaves the preview at the previous volume/balance.
        _audioPreview.ApplyMix(preset.MasterGain, preset.Balance);
        UpdateAntiClipReadout();
    }


    private void OnSoundSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        _workAudio.Clarity = ClaritySlider.Value;
        _workAudio.Ambience = AmbienceSlider.Value;
        _workAudio.Surround = SurroundSlider.Value;
        _workAudio.DynamicBoost = DynamicBoostSlider.Value;
        _workAudio.BassBoost = BassBoostSlider.Value;
        _workAudio.MasterGain = MasterGainSlider.Value;
        _workAudio.VolumeLeveling = LevelingSlider.Value;
        _workAudio.FilterQ = FilterQSlider.Value;
        _workAudio.Balance = BalanceSlider.Value;
        _workAudio.Name = "TUNED SOUND";
        UpdateSoundLabels(_workAudio);
        _audioPreview.ApplyMix(_workAudio.MasterGain, _workAudio.Balance);
    }


    private void OnBandChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        EnsureBands(_workAudio, _bandSliders.Count);
        for (int i = 0; i < _bandSliders.Count; i++)
        {
            _workAudio.Bands[i] = _bandSliders[i].Value;
            _bandValueBlocks[i].Text = Signed(_bandSliders[i].Value, "0.0");
        }

        _workAudio.NumBands = _bandSliders.Count;
        _workAudio.Name = "TUNED SOUND";
        UpdateAntiClipReadout();
    }


    private void OnBandCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || BandCountBox.SelectedItem is not int count)
        {
            return;
        }

        _workAudio.NumBands = count;
        EnsureBands(_workAudio, count);
        BuildBandStrip(count);
        LoadTune(_workDisplay, _workAudio);
    }


    private void OnAudioCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        AudioPreset? preset = FindAudio(id);
        if (preset is null)
        {
            return;
        }

        LoadTune(_workDisplay.Copy(), preset.Copy());
        _activeAudioId = preset.Id;
        HighlightCards();
        UpdateSoundLabels(preset);
    }


    private void OnAudioCardDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        _settings.CustomAudioPresets.RemoveAll(p => p.Id == id);
        Commit();
        BuildAudioCards();
        BuildSlots();
        Flash("[ PRESET DELETED ]", id.ToUpperInvariant());
    }


    private void OnAudioCardRename(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        AudioPreset? preset = _settings.CustomAudioPresets.FirstOrDefault(p => p.Id == id);
        if (preset is null)
        {
            return;
        }

        ShowModal("RENAME SOUND PRESET", preset.Name, name =>
        {
            preset.Name = name;
            Commit();
            BuildAudioCards();
            BuildSlots();
        });
    }


    private void OnSaveAsSoundClick(object sender, RoutedEventArgs e)
    {
        ShowModal("NAME YOUR SOUND PRESET", "MY SOUND", name =>
        {
            AudioPreset preset = _workAudio.Copy();
            preset.Id = AppProfileTools.NewId("sound");
            preset.Name = name;
            preset.Tag = "MINE";
            _settings.CustomAudioPresets.Add(preset);
            Commit();
            BuildAudioCards();
            BuildSlots();

            // Make the copy the loaded preset so Rename works on it straight away.
            _activeAudioId = preset.Id;
            LoadTune(_workDisplay, preset);
            HighlightCards();
            Flash("[ SOUND PRESET SAVED ]", name);
        });
    }


    private void OnAudioPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_audioPreview.IsPlaying)
        {
            _audioPreview.Pause();
        }
        else
        {
            _audioPreview.Play();
        }
    }


    private void UpdateAudioPreviewState()
    {
        bool playing = _audioPreview.IsPlaying;
        AudioPreviewButton.Content = playing ? "\u23F8" : "\u25B6";
        AudioPreviewStateText.Text = playing ? "PLAYING LOOP - STAYS ON UNTIL YOU PAUSE" : "PAUSED";
        AudioPreviewButton.ToolTip = playing ? "Pause the preview loop" : "Play the preview loop";
    }


    private void ApplyAudio(AudioPreset preset, bool announce)
    {
        _settings.ActiveAudioPresetId = preset.Id;
        _activeAudioId = preset.Id;
        UpdateSoundLabels(preset);
        HighlightCards();
        if (_audio.IsInstalled)
        {
            _audio.StartEngine();
            _audio.Apply(preset, SelectedDeviceName());
            SessionState.Current.AudioTouched = true;
            _liveAudioName = preset.Name.ToUpperInvariant();
        }
        else
        {
            UpdateFxBanner();
            _liveAudioName = "NOTHING (NO FXSOUND)";
        }

        UpdateLiveLabels();
        Commit();
        if (announce)
        {
            Flash("[ " + preset.Name + " ]", "SOUND READY");
        }
    }


    private void OnApplySoundClick(object sender, RoutedEventArgs e)
    {
        ApplyAudio(_workAudio.Copy(), true);
    }


    private void OnRenameSoundClick(object sender, RoutedEventArgs e)
    {
        AudioPreset? mine = _settings.CustomAudioPresets
            .FirstOrDefault(p => string.Equals(p.Id, _workAudio.Id, StringComparison.OrdinalIgnoreCase));

        if (mine is null)
        {
            Flash("[ BUILT IN PRESET ]", "USE SAVE AS TO MAKE YOUR OWN COPY");
            return;
        }

        ShowModal("RENAME SOUND PRESET", mine.Name, name =>
        {
            mine.Name = name;
            Commit();
            BuildAudioCards();
            BuildSlots();
        });
    }


    private void OnResetSoundClick(object sender, RoutedEventArgs e)
    {
        _ = ResetSoundAndRefresh();
    }


    private async System.Threading.Tasks.Task ResetSoundAndRefresh()
    {
        await _audio.ResetSoundAsync();
        SessionState.Current.AudioTouched = false;
        _workAudio = AudioPreset.Flat();
        LoadTune(_workDisplay, _workAudio);
        _activeAudioId = string.Empty;
        _liveAudioName = "NOTHING";
        UpdateLiveLabels();
        HighlightCards();
        Flash("[ SOUND RESET ]", "BACK TO NORMAL");
    }


    private void OnSaveInFxSoundClick(object sender, RoutedEventArgs e)
    {
        if (!_audio.IsInstalled)
        {
            UpdateFxBanner();
            Flash("[ NO FXSOUND ]", "INSTALL IT IN SETTINGS");
            return;
        }

        _audio.Apply(_workAudio, SelectedDeviceName());
        _audio.SavePresetInFxSound("GAMER TOOL");
        Flash("[ SAVED IN FXSOUND ]", "GAMER TOOL");
    }


    private void OnRescanDevicesClick(object sender, RoutedEventArgs e)
    {
        _audio.InvalidateCache();
        LoadDevices();
        RefreshFxState(true);
    }


    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceEvents || DeviceBox.SelectedItem is not DeviceChoice choice)
        {
            return;
        }

        _settings.OutputDeviceId = choice.Id;
        if (_audio.IsInstalled)
        {
            _audio.SetOutputDevice(choice.Id);
        }

        SelectCombo(SetupDeviceBox, choice.Id);
        Commit();
    }


    private void OnApplyDeviceClick(object sender, RoutedEventArgs e)
    {
        if (SetupDeviceBox.SelectedItem is not DeviceChoice choice)
        {
            return;
        }

        _settings.OutputDeviceId = choice.Id;
        _settings.OutputDeviceName = choice.Name;
        SelectCombo(DeviceBox, choice.Id);
        if (_audio.IsInstalled)
        {
            _audio.SetOutputDevice(choice.Id);
        }

        Commit();
        Flash("[ DEVICE SET ]", choice.Name.ToUpperInvariant());
    }


    private static void SelectCombo(ComboBox box, string id)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is DeviceChoice choice && string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }


    private void RefreshFxState(bool force)
    {
        if (!_audio.IsInstalled)
        {
            LivePowerText.Text = "NOT INSTALLED";
            LivePowerText.Foreground = (Brush)FindResource("Amber");
            LivePresetText.Text = "SETTINGS > SOUND ENGINE";
            return;
        }

        FxSoundState? state = _audio.ReadState(force);
        if (state is null)
        {
            LivePowerText.Text = "ENGINE ASLEEP";
            LivePresetText.Text = "START IT IN SETTINGS";
            return;
        }

        LivePowerText.Text = state.Power ? "POWER ON" : "POWER OFF";
        LivePowerText.Foreground = (Brush)FindResource(state.Power ? "Green" : "Amber");
        LivePresetText.Text = state.SelectedPreset.ToUpperInvariant();
        LiveEngineText.Text = "MASTER " + Signed(state.Equalizer.MasterGain, "0");
        LiveEngineSubText.Text = "STEADY " + state.Equalizer.VolumeLeveling.ToString("0.0", CultureInfo.InvariantCulture)
            + "   Q " + state.Equalizer.FilterQ.ToString("0.0", CultureInfo.InvariantCulture)
            + "   BAL " + Signed(state.Equalizer.Balance, "0");
        LiveOutputText.Text = string.IsNullOrWhiteSpace(state.SelectedOutput) ? "NO OUTPUT" : state.SelectedOutput.ToUpperInvariant();
        LiveVersionText.Text = "FXSOUND " + state.Version;

        for (int i = 0; i < _bandLabelBlocks.Count && i < state.Equalizer.Bands.Count; i++)
        {
            _bandLabelBlocks[i].Text = state.Equalizer.Bands[i].FrequencyText;
        }
    }


    private void OnAntiClipChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.AntiClip = AntiClipBox.IsChecked == true;
        _audio.AntiClipEnabled = _settings.AntiClip;
        UpdateAntiClipReadout();
        Commit();
    }


    private void UpdateAntiClipReadout()
    {
        if (_workAudio is null)
        {
            return;
        }

        double headroom = AudioService.RequiredHeadroom(_workAudio);
        if (!_settings.AntiClip)
        {
            AntiClipValue.Text = "PREAMP OFF";
            return;
        }

        double effective = AudioService.EffectiveMasterGain(_workAudio, true);
        AntiClipValue.Text = headroom <= 0.0
            ? "PREAMP 0.0 dB"
            : "PREAMP " + (-headroom).ToString("0.0", CultureInfo.InvariantCulture) + " dB   GAIN " + effective.ToString("0.0", CultureInfo.InvariantCulture);
    }


}
