using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Brush = System.Windows.Media.Brush;
using AudioSlider = System.Windows.Controls.Slider;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Figure = System.Windows.Media.PathFigure;
using LineSegment = System.Windows.Media.LineSegment;
using PathGeometry = System.Windows.Media.PathGeometry;
using PenLineCap = System.Windows.Media.PenLineCap;
using PenLineJoin = System.Windows.Media.PenLineJoin;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using Path = System.Windows.Shapes.Path;
using TextBlock = System.Windows.Controls.TextBlock;

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
            int index = choices.FindIndex(c => string.Equals(c.Id, _settings.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
            DeviceBox.SelectedIndex = index < 0 ? 0 : index;
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
        string defaultOutput = _devices.GetDefaultOutputName();
        if (selected.Length == 0 || !LooksLikeDisplayOutput(selected))
        {
            return;
        }

        if (defaultOutput.Length == 0 || !LooksLikeDisplayOutput(defaultOutput))
        {
            return;
        }

        List<string> known = _audio.IsInstalled
            ? _audio.GetOutputDevices().ToList()
            : _devices.GetOutputDevices().Select(d => d.Name).ToList();

        List<AudioDeviceInfo> real = _devices.GetOutputDevices()
            .Where(device => !LooksLikeDisplayOutput(device.Name))
            .Where(device => known.Any(k => k.Contains(device.Name, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (real.Count == 0)
        {
            Flash("No speakers found, pick an output", true);
            return;
        }

        if (real.Count > 1)
        {
            Flash("Pick your speakers in Settings", true);
            return;
        }

        // With FxSound installed the settings box is keyed by device name, and
        // without it the box is keyed by endpoint id, so pick the matching half.
        _settings.OutputDeviceId = _audio.IsInstalled ? real[0].Name : real[0].Id;
        _profiles.Save(_settings);
        LoadDevices();
        Flash("Output set to " + real[0].Name);
    }


    private static readonly string[] DisplayOutputHints =
    {
        "AG276", "AMD High Definition", "Display Audio", "Monitor",
        "HDMI", "DisplayPort", "DP ", "TV", "Intel Display"
    };


    /// <summary>
    /// True for HDMI or DisplayPort audio on a monitor. Routing game audio to one
    /// of these is silent, because the endpoint has no speakers behind it.
    /// </summary>
    private static bool LooksLikeDisplayOutput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (string hint in DisplayOutputHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            FxStateText.Text = "Not installed";
            FxStateText.Foreground = (Brush)FindResource("Amber");
            FxPathText.Text = "FxSound is not on this PC yet";
            FxVersionText.Text = "Every audio control on the Audio tab needs it";
            FxInstallButton.Visibility = Visibility.Visible;
            FxStartEngineButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            FxStateText.Text = "Ready";
            FxStateText.Foreground = (Brush)FindResource("Green");
            FxPathText.Text = _audio.ExePath;
            FxVersionText.Text = _fxUpdateAvailable
                ? "Version " + _fxUpdateVersion + " is out"
                : "Up to date";
            FxInstallButton.Visibility = Visibility.Collapsed;
            FxStartEngineButton.Visibility = Visibility.Visible;
        }

        FxUpgradeButton.Visibility = !missing && _fxUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (_fxUpdateAvailable && _fxUpdateVersion.Length > 0)
        {
            FxUpgradeButton.Content = "\U0001F504  Update to " + _fxUpdateVersion;
        }

        UpdatePresetChrome();
    }


    private void BuildBandStrip(int count)
    {
        BandGrid.Children.Clear();
        // One column per band: the fader template scales to the cell, so 5 bands
        // come out chunky and 31 come out narrow, and every count stays inside
        // the column instead of wrapping into a clipped second row.
        BandGrid.Columns = count;
        _bandSliders.Clear();
        _bandValueBlocks.Clear();
        _bandLabelBlocks.Clear();
        _frequencyDials.Clear();

        double fader = Math.Clamp(300.0 / Math.Max(count, 1), 12.0, 30.0);

        // The engine only opens up a centre frequency for a band at 5 and 10
        // bands. Every other count has one legal frequency per band, so a dial
        // there would either do nothing or let two bands collide.
        bool dials = AudioPreset.HasFrequencyDial(count);
        // Sized against the engine's own dials, which are wide enough to grab and
        // read at a glance rather than being a token ring under each band.
        double dialSize = dials ? 38.0 : 0.0;

        for (int i = 0; i < count; i++)
        {
            Grid cell = new();
            cell.Margin = new Thickness(1, 0, 1, 0);
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (dials)
            {
                cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            TextBlock value = new()
            {
                Style = (Style)FindResource("Value"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = "0.0",
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 3)
            };

            var slider = new BandFader
            {
                Style = (Style)FindResource("BandSliderVertical"),
                Minimum = AudioPreset.GainMin,
                Maximum = AudioPreset.GainMax,
                Foreground = (Brush)FindResource("AccentAudio"),
                Width = fader,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0),

                // The engine draws its bands as dashed lines with nothing at the
                // zero point, so the strip asks for that rather than the solid
                // groove the colour trims keep.
                Dashed = true
            };

            // A band at 0 dB is flat, so that is where its magnet sits.
            SliderSnap.SetNeutral(slider, 0.0);
            SliderSnap.SetThreshold(slider, 0.4);
            slider.ValueChanged += OnBandChanged;

            TextBlock label = new()
            {
                Style = (Style)FindResource("Label"),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = (FontFamily)FindResource("Mono"),
                FontSize = 9.5,
                Foreground = (Brush)FindResource("TextLow"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = AudioPreset.FormatFrequency(AudioPreset.BandFrequency(count, i))
            };

            Grid.SetRow(value, 0);
            Grid.SetRow(slider, 1);
            Grid.SetRow(label, 2);

            cell.Children.Add(value);
            cell.Children.Add(slider);
            cell.Children.Add(label);

            if (dials)
            {
                AudioPreset.BandWindow window = AudioPreset.Window(count, i);
                int steps = AudioPreset.Steps(window);
                int index = i;

                var dial = new FrequencyDial
                {
                    Accent = (Brush)FindResource("AccentAudio"),
                    Steps = steps,
                    Step = AudioPreset.StepFromFrequency(window, AudioPreset.BandFrequency(count, i)),
                    DefaultStep = AudioPreset.StepFromFrequency(window, AudioPreset.BandFrequency(count, i)),
                    Width = dialSize,
                    Height = dialSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 3, 0, 0)
                };

                void OnDialChanged(object? s, EventArgs args) => OnFrequencyChanged(index);

                dial.ValueChanged += OnDialChanged;
                Grid.SetRow(dial, 3);
                cell.Children.Add(dial);
                _frequencyDials.Add(dial);
            }

            BandGrid.Children.Add(cell);

            _bandSliders.Add(slider);
            _bandValueBlocks.Add(value);
            _bandLabelBlocks.Add(label);
        }
    }


    /// <summary>
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
        _workAudio.Name = "Tuned";
        UpdateSoundLabels(_workAudio);
        UpdatePresetChrome();
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
        _workAudio.Name = "Tuned";
        UpdateAntiClipReadout();
        UpdatePresetChrome();
        UpdateEqCurve();
    }


    /// <summary>
    /// Commits a centre frequency the dial moved to, and puts the number back in
    /// the label above it. The frequency is clamped to the band's own window,
    /// which is what stops two bands drifting onto the same part of the spectrum.
    /// </summary>
    private void OnFrequencyChanged(int index)
    {
        if (!_ready || _updating || index < 0 || index >= _frequencyDials.Count)
        {
            return;
        }

        int count = _frequencyDials.Count;
        if (!AudioPreset.HasFrequencyDial(count))
        {
            return;
        }

        AudioPreset.BandWindow window = AudioPreset.Window(count, index);
        double hz = AudioPreset.FrequencyFromStep(window, _frequencyDials[index].Step);

        EnsureBands(_workAudio, count);
        EnsureFrequencies(_workAudio, count);
        _workAudio.Frequencies[index] = hz;

        if (index < _bandLabelBlocks.Count)
        {
            _bandLabelBlocks[index].Text = AudioPreset.FormatFrequency(hz);
        }

        _workAudio.NumBands = count;
        _workAudio.Name = "Tuned";
        UpdatePresetChrome();
        UpdateEqCurve();
    }


    /// <summary>Grows a preset's stored frequencies so a band can be retuned.</summary>
    private static void EnsureFrequencies(AudioPreset preset, int count)
    {
        if (preset.Frequencies.Length >= count)
        {
            return;
        }

        var grown = new double[count];
        for (int i = 0; i < count; i++)
        {
            grown[i] = preset.Frequencies.Length > i && preset.Frequencies[i] > 0.0
                ? preset.Frequencies[i]
                : AudioPreset.BandFrequency(preset.NumBands > 0 ? preset.NumBands : count, i);
        }

        preset.Frequencies = grown;
    }


    private void OnEqCurveCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateEqCurve();
    }

    /// <summary>
    /// Redraws the line that connects the band faders, plus a soft fill under it.
    ///
    /// The points come from the live slider geometry rather than from the gain
    /// numbers, so the curve stays glued to the faders at any band count, any
    /// window size and any fader width. A vertical slider puts its maximum at the
    /// top of its own height, so the mapping is just the fraction of travel.
    /// </summary>
    private void UpdateEqCurve()
    {
        Canvas canvas = EqCurveCanvas;
        canvas.Children.Clear();

        if (_bandSliders.Count < 2)
        {
            return;
        }

        List<Point> points = new(_bandSliders.Count);
        foreach (AudioSlider slider in _bandSliders)
        {
            if (slider.ActualHeight <= 0 || !slider.IsVisible)
            {
                return;
            }

            Point top = slider.TranslatePoint(new Point(0, 0), canvas);
            double travel = slider.ActualHeight;
            double fraction = (slider.Value - slider.Minimum) / Math.Max(slider.Maximum - slider.Minimum, 0.001);
            points.Add(new Point(top.X + (slider.ActualWidth / 2.0), top.Y + (travel * (1.0 - fraction))));
        }

        Brush accent = (Brush)FindResource("AccentAudio");

        // The fill stops at the faders' own top and bottom, not the canvas, or it
        // washes over the frequency labels and greys them out. Every fader sits in
        // the same row with the same height, so the first one sets the band.
        AudioSlider first = _bandSliders[0];
        double bandTop = first.TranslatePoint(new Point(0, 0), canvas).Y;
        double bandBottom = bandTop + first.ActualHeight;

        // The fill first, so the line sits on top of it.
        PathGeometry fill = new();
        Figure fillFigure = new() { IsClosed = true, IsFilled = true };
        fillFigure.StartPoint = new Point(points[0].X, bandBottom);
        for (int i = 0; i < points.Count; i++)
        {
            fillFigure.Segments.Add(new LineSegment(points[i], true));
        }

        fillFigure.Segments.Add(new LineSegment(new Point(points[^1].X, bandBottom), true));
        fill.Figures.Add(fillFigure);

        // Only the geometry and the brush are frozen. A Shape is a
        // FrameworkElement, which is not a Freezable, so freezing the element
        // itself is not an option.
        fill.Freeze();

        // The gradient is anchored to the panel in absolute coordinates, so it
        // stays put while the bands move: the fill is brightest just under the top
        // of the band area and gone by the bottom, exactly as the engine draws it.
        //
        // The mapping mode has to be Absolute. A gradient brush measures its stops
        // as fractions of the bounding box by default, so handing it pixel
        // coordinates pushes every stop past the end of the range and the whole
        // fill comes out one flat opacity.
        LinearGradientBrush wash = new(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x66, 0xFF, 0x2D, 0x6F), 0.0),
                new GradientStop(Color.FromArgb(0x2E, 0xFF, 0x2D, 0x6F), 0.45),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0x2D, 0x6F), 1.0)
            },
            new Point(0, bandTop),
            new Point(0, bandBottom));
        wash.MappingMode = BrushMappingMode.Absolute;
        wash.Freeze();

        canvas.Children.Add(new Path
        {
            Data = fill,
            Fill = wash,
            Stroke = Brushes.Transparent,
            IsHitTestVisible = false
        });

        PathGeometry line = new();
        Figure lineFigure = new() { IsClosed = false, IsFilled = false };
        lineFigure.StartPoint = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            lineFigure.Segments.Add(new LineSegment(points[i], true));
        }

        line.Figures.Add(lineFigure);
        line.Freeze();

        // The line fades with its height on the panel, so the part of the curve up
        // near a boost is bright and the part down near a cut is faint. Measuring
        // it in absolute coordinates keeps the fade anchored to the panel while
        // the bands move, the same as the fill beneath it.
        Color lineColour = ((SolidColorBrush)accent).Color;
        LinearGradientBrush lineBrush = new(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0xFF, lineColour.R, lineColour.G, lineColour.B), 0.0),
                new GradientStop(Color.FromArgb(0x8C, lineColour.R, lineColour.G, lineColour.B), 1.0)
            },
            new Point(0, bandTop),
            new Point(0, bandBottom));
        lineBrush.MappingMode = BrushMappingMode.Absolute;
        lineBrush.Freeze();

        canvas.Children.Add(new Path
        {
            Data = line,
            Stroke = lineBrush,
            StrokeThickness = 1.9,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        });
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

        // BuildBandStrip makes new faders, so the old curve is stale. LoadTune
        // already queues a redraw, but only after these exist.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(UpdateEqCurve));
    }


    private void OnAudioPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || AudioPresetBox.SelectedItem is not PresetChoice choice)
        {
            return;
        }

        AudioPreset? preset = FindAudio(choice.Id);
        if (preset is null)
        {
            return;
        }

        _activeAudioId = preset.Id;
        LoadTune(_workDisplay.Copy(), preset.Copy());
        UpdatePresetChrome();
    }


    private void OnDeleteSoundClick(object sender, RoutedEventArgs e)
    {
        AudioPreset? mine = _settings.CustomAudioPresets
            .FirstOrDefault(p => string.Equals(p.Id, _activeAudioId, StringComparison.OrdinalIgnoreCase));

        if (mine is null)
        {
            Flash("Built in presets stay put");
            return;
        }

        string removed = mine.Name;
        ShowConfirmModal(
            "DELETE \u201C" + removed.ToUpperInvariant() + "\u201D?",
            "This takes the preset out of Gamer Tool and out of any slot that points at it.",
            "Delete",
            () =>
            {
                _settings.CustomAudioPresets.RemoveAll(p => p.Id == mine.Id);
                _activeAudioId = string.Empty;
                Commit();
                RefreshPresetBoxes();
                LoadTune(_workDisplay, AudioPreset.Flat());
                BuildSlots();
                Flash(removed + " deleted");
            });
    }


    private void OnSaveAsSoundClick(object sender, RoutedEventArgs e)
    {
        ShowModal("NAME THIS SOUND PRESET", "My sound", name =>
        {
            AudioPreset preset = _workAudio.Copy();
            preset.Id = AppProfileTools.NewId("sound");
            preset.Name = name;
            preset.Tag = "Mine";
            _settings.CustomAudioPresets.Add(preset);
            _activeAudioId = preset.Id;
            Commit();
            RefreshPresetBoxes();
            BuildSlots();
            Flash(name + " saved");
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


    /// <summary>
    /// Steps the preview loop through gameplay, music and footsteps. A curve tuned
    /// for one kind of material can leave another sounding thin, so all three are
    /// needed to hear what a preset actually does.
    /// </summary>
    private void OnPreviewTrackClick(object sender, RoutedEventArgs e)
    {
        _audioPreview.SetTrack(_audioPreview.NextTrack());
        UpdatePreviewTrackButton();
    }


    /// <summary>
    /// Shows the sample that is loaded rather than the one a click would switch
    /// to. Showing the target read as though the icons were in the wrong order,
    /// because the music note appeared to be playing the game sample.
    /// </summary>
    private void UpdatePreviewTrackButton()
    {
        (string glyph, string tip) = _audioPreview.Track switch
        {
            AudioPreviewPlayer.PreviewTrack.Music => ("music", "Music"),
            AudioPreviewPlayer.PreviewTrack.Footsteps => ("footsteps", "Footsteps"),
            _ => ("gamepad", "Game")
        };

        PreviewTrackGlyph.Glyph = glyph;
        PreviewTrackButton.ToolTip = tip;
        PreviewTrackButton.Background = _audioPreview.Track == AudioPreviewPlayer.PreviewTrack.Game
            ? (Brush)FindResource("Elevated")
            : (Brush)FindResource("AccentAudioGlow");
    }


    /// <summary>
    /// Swaps the glyph on the preview button. Play and pause are drawn as outlines
    /// rather than taken as emoji, because U+25B6 and U+23F8 are geometric glyphs
    /// with no colour, and a stroke stays sharp and picks up the tab accent.
    /// </summary>
    private void UpdateAudioPreviewState()
    {
        bool playing = _audioPreview.IsPlaying;
        AudioPreviewButton.Icon = playing ? Icons.Pause : Icons.Play;
        AudioPreviewButton.ToolTip = playing ? "Pause" : "Play";
    }


    private void ApplyAudio(AudioPreset preset, bool announce)
    {
        _settings.ActiveAudioPresetId = preset.Id;
        _activeAudioId = preset.Id;
        UpdateSoundLabels(preset);
        UpdatePresetChrome();
        if (_audio.IsInstalled)
        {
            _audio.StartEngine();
            _audio.Apply(preset, SelectedDeviceName());
            SessionState.Current.AudioTouched = true;
            _liveAudioName = preset.Name.ToUpperInvariant();

            // Read the engine back and say what it is really doing, rather than
            // reporting the launch as though the launch were the change. It waits
            // on the engine writing its state file, so it runs off the UI thread
            // and the answer arrives a moment later.
            _ = VerifyApplyAsync(preset);
        }
        else
        {
            UpdateFxBanner();
        }

        Commit();
        if (announce)
        {
            Flash(preset.Name + " applied");
        }
    }


    private async System.Threading.Tasks.Task VerifyApplyAsync(AudioPreset preset)
    {
        string device = SelectedDeviceName();
        AudioService.ApplyReport report =
            await System.Threading.Tasks.Task.Run(() => _audio.Verify(preset, device));
        ReportApply(report);
    }


    private void OnApplySoundClick(object sender, RoutedEventArgs e)
    {
        ApplyAudio(_workAudio.Copy(), true);
    }


    private void OnRenameSoundClick(object sender, RoutedEventArgs e)
    {
        AudioPreset? mine = _settings.CustomAudioPresets
            .FirstOrDefault(p => string.Equals(p.Id, _activeAudioId, StringComparison.OrdinalIgnoreCase));

        if (mine is null)
        {
            Flash("Built in presets cannot be renamed", true);
            return;
        }

        ShowModal("RENAME SOUND PRESET", mine.Name, name =>
        {
            mine.Name = name;
            _workAudio.Name = name;
            _activeAudioId = mine.Id;
            Commit();
            RefreshPresetBoxes();
            UpdatePresetChrome();
            BuildSlots();
        });
    }

    /// <summary>
    /// The one definition of "back to normal" for the sound, shared by the reset
    /// button, the tray menu and the global off hotkey.
    ///
    /// The band layout is kept, because a reset should zero the gains rather than
    /// quietly drop someone off thirty one bands and back to ten.
    /// </summary>
    public async System.Threading.Tasks.Task GoSoundNeutralAsync()
    {
        // Off the dispatcher first. ResetSoundAsync is three engine calls that
        // each wait on a child process, and this is reached from paths where the
        // user is waiting on the result: a hotkey, the reset button, and now a
        // game closing. The window work after it goes back across explicitly, so
        // the only thing that moves off the UI thread is the blocking part.
        try
        {
            await Task.Run(() => _audio.ResetSoundAsync());
        }
        catch (Exception ex)
        {
            TraceLog.Write("SOUND RESET", ex);
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SessionState.Current.AudioTouched = false;
                _liveAudioName = "FLAT";
                _activeAudioId = "flat";

                // Read here rather than before the wait, so it is the band count
                // the panel has now rather than the one it had several seconds ago.
                int bands = _bandSliders.Count > 0 ? _bandSliders.Count : AudioPreset.PresetBandCount;
                LoadTune(_workDisplay, AudioPreset.Flat(bands));
                RefreshPresetBoxes();
                UpdateLiveLabels();
            });
        }
        catch (Exception ex)
        {
            // Only reachable if the window is shutting down underneath the reset.
            TraceLog.Write("SOUND RESET UI", ex);
        }
    }


    private async void OnResetSoundClick(object sender, RoutedEventArgs e)
    {
        await GoSoundNeutralAsync();
        Flash("Sound back to normal");
    }


    private void OnSaveInFxSoundClick(object sender, RoutedEventArgs e)
    {
        if (!_audio.IsInstalled)
        {
            UpdateFxBanner();
            Flash("Install FxSound from Settings first", true);
            return;
        }

        _audio.Apply(_workAudio, SelectedDeviceName());
        _audio.SavePresetInFxSound("Gamer Tool");
        Flash("Saved in FxSound as Gamer Tool");
    }


    private void OnRescanDevicesClick(object sender, RoutedEventArgs e)
    {
        _audio.InvalidateCache();
        LoadDevices();
        _ = RefreshFxStateAsync(true);
    }


    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceEvents || DeviceBox.SelectedItem is not DeviceChoice choice)
        {
            return;
        }

        _settings.OutputDeviceId = choice.Id;
        _settings.OutputDeviceName = choice.Name;
        if (_audio.IsInstalled)
        {
            _audio.SetOutputDevice(choice.Id);
        }

        Commit();
    }


    /// <summary>
    /// Polls the engine and refreshes what the page actually shows. The only thing
    /// worth taking from it is the live band frequencies, because those drive the
    /// labels under the faders. Everything else the old footer printed duplicated
    /// something already on screen, so it is not read at all.
    ///
    /// It is also where the applied curve gets checked. Per band gains and centre
    /// frequencies are both documented running instance parameters, so the curve
    /// goes across as a command and is read straight back out of the engine. When
    /// the engine does not agree, the panel says so instead of the toast claiming
    /// a change that never happened.
    ///
    /// The read itself is slow enough to matter: it starts the engine, waits for
    /// it, then polls its status file, all synchronously, and this runs on a four
    /// second timer for the life of the process. It is therefore handed to the
    /// thread pool and only the result comes back, rather than the dispatcher
    /// standing in front of the engine.
    /// </summary>
    private async Task RefreshFxStateAsync(bool force)
    {
        if (!_audio.IsInstalled)
        {
            return;
        }

        // One read at a time. The poll interval is shorter than the worst case
        // read, so without this the timer can start a second read while the first
        // is still inside the engine and the two race over the cached state.
        if (Interlocked.Exchange(ref _fxStateBusy, 1) == 1)
        {
            return;
        }

        try
        {
            FxSoundState? state = await Task.Run(() => _audio.ReadState(force));
            if (state is null)
            {
                return;
            }

            // Everything past this point touches live controls, so it is put back
            // on the dispatcher explicitly. The await above already resumes there,
            // but naming the crossing point keeps that true even if this is ever
            // called from a thread with no synchronisation context of its own.
            await Dispatcher.InvokeAsync(() => ApplyEngineState(state));
        }
        catch (Exception ex)
        {
            // Includes the window shutting down underneath a read in flight.
            TraceLog.Write("FX STATE", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _fxStateBusy, 0);
        }
    }


    /// <summary>
    /// The window half of a poll, and the only part allowed to touch controls.
    /// Runs on the dispatcher, so nothing in here may block.
    /// </summary>
    private void ApplyEngineState(FxSoundState state)
    {
        // The frequency labels are only borrowed from the engine when the page
        // cannot edit them. Where a dial exists the app's own tuning owns the
        // label, because the engine still reports the previous frequency until
        // the new curve is applied, which would otherwise undo the edit on screen.
        if (_frequencyDials.Count == 0 && state.Equalizer.Bands.Count == _bandSliders.Count)
        {
            for (int i = 0; i < _bandLabelBlocks.Count && i < state.Equalizer.Bands.Count; i++)
            {
                _bandLabelBlocks[i].Text = state.Equalizer.Bands[i].FrequencyText;
            }
        }

        CheckCurveApplied(state);
    }


    /// <summary>
    /// Compares what the engine reports against the tune on screen, and speaks up
    /// only when the answer changes, so a steady mismatch does not nag.
    /// </summary>
    private void CheckCurveApplied(FxSoundState state)
    {
        int count = _bandSliders.Count;
        if (count == 0 || state.Equalizer.Bands.Count < count)
        {
            return;
        }

        bool matches = true;
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(state.Equalizer.Bands[i].Gain - _bandSliders[i].Value) > 0.1)
            {
                matches = false;
                break;
            }
        }

        // The first poll after launch is ignored: nothing has been applied yet, so
        // a mismatch there is expected rather than a failure.
        if (!_curveChecked)
        {
            _curveChecked = true;
            _curveApplied = matches;
            return;
        }

        if (matches == _curveApplied)
        {
            return;
        }

        _curveApplied = matches;
        if (matches)
        {
            Flash("Engine is on this sound");
        }
        else
        {
            Flash("Engine did not take this sound", true);
        }
    }


    /// <summary>Whether the engine currently reports the curve shown on screen.</summary>
    private bool _curveApplied = true;

    /// <summary>Set once the first poll has happened, so launch is not reported as a failure.</summary>
    private bool _curveChecked;

    /// <summary>1 while an engine read is in flight, so two of them never overlap.</summary>
    private int _fxStateBusy;


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

        if (!_settings.AntiClip)
        {
            AntiClipValue.Text = "Preamp off";
            return;
        }

        double headroom = AudioService.RequiredHeadroom(_workAudio);
        double effective = AudioService.EffectiveMasterGain(_workAudio, true);
        AntiClipValue.Text = headroom <= 0.0
            ? "Preamp 0.0 dB"
            : "Preamp " + (-headroom).ToString("0.0", CultureInfo.InvariantCulture)
              + " dB   gain " + effective.ToString("0.0", CultureInfo.InvariantCulture);
    }

}
