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
    /// <summary>
    /// Works out what the output list should contain, touching nothing on screen.
    /// <para>
    /// Kept separate from <see cref="LoadDevices"/> because the interesting half
    /// of it starts the engine, waits for it and polls its state file, which is
    /// slow enough that doing it inline held the window up on launch. This half
    /// reads services only, so it can run on a worker; the control assignment
    /// cannot, and stays where it is.
    /// </para>
    /// </summary>
    private List<DeviceChoice> BuildDeviceChoices()
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

        return choices;
    }

    private void LoadDevices()
    {
        _suppressDeviceEvents = true;
        try
        {
            LoadDevicesFrom(BuildDeviceChoices());
        }
        finally
        {
            _suppressDeviceEvents = false;
        }
    }

    /// <summary>Fills the picker from a list that was worked out elsewhere.</summary>
    private void LoadDevicesFrom(List<DeviceChoice> choices)
    {
        _suppressDeviceEvents = true;
        try
        {
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
    /// What <see cref="EnsureUsableAudioOutput"/> decided, and why.
    /// <para>
    /// The chosen device comes back as a whole <see cref="DeviceChoice"/> rather
    /// than as a name. The settings box is keyed differently depending on whether
    /// FxSound is installed, by name when it is and by endpoint id when it is not,
    /// so turning a name into something storable needs the same device list
    /// <see cref="BuildDeviceChoices"/> reads. This method has already read it.
    /// Returning a bare name forced the caller to enumerate every output device a
    /// second time, on the dispatcher, which is precisely the freeze the
    /// off-thread startup work exists to avoid.
    /// </para>
    /// </summary>
    private readonly record struct OutputRepair(DeviceChoice? Choice, string? Message, bool Warn);

    /// <summary>
    /// Everything the app has to do once the engine is present: re-read the output
    /// list, repair the output if the install moved it, and put the current tune
    /// back and read it back out to prove it took.
    /// <para>
    /// The reads go on a worker because they are genuinely slow right after an
    /// install. The cache is dropped first on purpose, so the first status read
    /// polls the engine's state file for up to twelve 150 ms slices. On the
    /// dispatcher that is a visible freeze at exactly the wrong moment: the user
    /// has just finished an installer and is looking at a dead window before the
    /// "it worked" dialog even appears.
    /// </para>
    /// </summary>
    /// <param name="readyMessage">Flash text shown once the engine has answered.</param>
    /// <returns>
    /// True when the engine was reached. Callers put a claim in front of the user
    /// about the app talking to FxSound, and that claim should not be made when
    /// this returned false.
    /// </returns>
    private async System.Threading.Tasks.Task<bool> HandshakeWithEngineAsync(string readyMessage)
    {
        try
        {
            List<DeviceChoice> choices = await System.Threading.Tasks.Task.Run(() =>
            {
                _audio.InvalidateCache();
                return BuildDeviceChoices();
            });

            OutputRepair repair = await System.Threading.Tasks.Task.Run(DetectOutputRepair);

            await Dispatcher.InvokeAsync(() =>
            {
                LoadDevicesFrom(choices);

                // Handing the list over keeps the repair from enumerating the
                // output devices a second time on the dispatcher.
                EnsureUsableAudioOutput(repair, choices);
                _ = RefreshFxStateAsync(true);
                UpdateFxBanner();
                ApplyAudio(_workAudio.Copy(), false);
                Flash(readyMessage);
            });

            return true;
        }
        catch (Exception ex)
        {
            // Includes the window closing underneath the read.
            TraceLog.Write("ENGINE HANDSHAKE", ex);
            Flash("FxSound is installed but Gamer Tool could not reach it", true);
            return false;
        }
    }

    /// <param name="known">
    /// The device list the caller already enumerated and already put in the
    /// picker, or null when the list has to be built here. Passing it matters on
    /// the startup path: the repair only changes which entry is selected, not what
    /// the list contains, so enumerating again would repeat work already done.
    /// </param>
    private void EnsureUsableAudioOutput(OutputRepair repair, List<DeviceChoice>? known)
    {
        if (repair.Choice is null && repair.Message is null)
        {
            return;
        }

        if (repair.Choice is DeviceChoice choice)
        {
            // Id is already the right key for whichever half of the app is in use,
            // because DetectOutputRepair built it that way.
            _settings.OutputDeviceId = choice.Id;
            _settings.OutputDeviceName = choice.Name;
            _profiles.Save(_settings);

            if (known is null)
            {
                LoadDevices();
            }
            else
            {
                // Only add it if the two enumerations disagreed, which would mean a
                // device appeared in between. The comparison has to match the one
                // LoadDevicesFrom makes or the selection will not land.
                if (!known.Any(c => string.Equals(c.Id, choice.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    known.Add(choice);
                }

                LoadDevicesFrom(known);
            }

            Flash("Output set to " + choice.Name);
            return;
        }

        if (repair.Message is not null)
        {
            Flash(repair.Message, repair.Warn);
        }
    }

    /// <summary>
    /// FxSound follows the Windows default playback endpoint. When that default is
    /// monitor/HDMI audio there are no speakers, so every preset sounds muted. The
    /// decision about what to do about that lives in
    /// <see cref="OutputRouting.Decide"/>, which is pure and therefore testable;
    /// this only gathers the values it needs.
    /// <para>
    /// Only reads, so it can run off the dispatcher at launch. The endpoint list is
    /// read once and shared, because reading it was the other hidden cost here.
    /// </para>
    /// </summary>
    private OutputRepair DetectOutputRepair()
    {
        string selected = FxSoundState.TryRead()?.SelectedOutput ?? string.Empty;
        string defaultOutput = _devices.GetDefaultOutputName();
        bool installed = _audio.IsInstalled;

        IReadOnlyList<AudioDeviceInfo> endpoints = _devices.GetOutputDevices();
        List<string> known = installed
            ? _audio.GetOutputDevices().ToList()
            : endpoints.Select(d => d.Name).ToList();

        OutputRouting.Decision decision = OutputRouting.Decide(
            _settings.OutputDeviceId,
            selected,
            defaultOutput,
            endpoints,
            known,
            installed);

        return decision.Action switch
        {
            OutputRouting.Action.Switch => new OutputRepair(
                new DeviceChoice { Id = decision.TargetId!, Name = decision.TargetName! },
                null,
                false),
            OutputRouting.Action.Warn => new OutputRepair(null, decision.Message, true),
            _ => new OutputRepair(null, null, false),
        };
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

        // Half a step, so the magnet only takes hold of a value that is genuinely
        // nearer zero than the finest real setting. It used to be 0.4, which was
        // right for a half decibel step and quietly ate the quarter ones: 0.25 is
        // within 0.4 of zero, so +0.25 and -0.25 both snapped back to flat and the
        // finer step could not be reached at all.
        SliderSnap.SetThreshold(slider, 0.125);

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

                // Names the band and gives the dial its range in hertz, so the
                // read-back a screen reader does is in the unit the label under
                // the fader is in rather than in detents.
                dial.SetBand(
                    "Band " + (i + 1).ToString(CultureInfo.InvariantCulture) + " frequency",
                    window.Min,
                    window.Max,
                    AudioPreset.BandFrequency(count, i));

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
            _bandValueBlocks[i].Text = Signed(preset.Band(i), "0.00");
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
            _bandValueBlocks[i].Text = Signed(_bandSliders[i].Value, "0.00");
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
    /// Draws the line that connects the band faders, plus a soft fill for the
    /// parts of the curve that are off zero.
    ///
    /// The points come from the live slider geometry rather than from the gain
    /// numbers, so the curve stays glued to the faders at any band count, any
    /// window size and any fader width. A vertical slider puts its maximum at the
    /// top of its own height, so the mapping is just the fraction of travel.
    /// <para>
    /// The fill is the part that used to be wrong. It was the whole region between
    /// the curve and the bottom of the band area, so every band sitting at zero
    /// meant a solid block of colour filling the bottom half of the panel. The
    /// faders draw a band at rest as an empty groove, so the fill was stating the
    /// opposite of the thing directly above it, and it was the loudest element on
    /// the tab while standing for no change at all.
    /// </para>
    /// <para>
    /// So the fill is only drawn where the curve has actually left zero, and it
    /// closes to the zero line rather than to the floor. A boost fills upwards
    /// from the middle and a cut fills downwards, which is the reading a hardware
    /// analyser gives and the one that agrees with the faders. With every band at
    /// rest there is nothing to fill and the panel is empty, which is what the
    /// faders are saying too.
    /// </para>
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

            // Where the knob is, rather than where the value says it should be. A
            // vertical Track holds a thumb's centre half a thumb inside each end, so
            // scaling the value across the full height drew the curve up to eleven
            // pixels from the knob, worst at +12 and -12. The knob cannot be moved
            // out of that inset without losing the drag handle, so the curve goes
            // to the knob. The arithmetic is the fallback for the moment between a
            // fader being created and its template being applied.
            Point top = slider.TranslatePoint(new Point(0, 0), canvas);
            double travel = slider.ActualHeight;
            double fraction = (slider.Value - slider.Minimum) / Math.Max(slider.Maximum - slider.Minimum, 0.001);
            System.Windows.Point? knob = (slider as BandFader)?.KnobCentre();
            points.Add(new Point(
                top.X + (knob?.X ?? (float)(slider.ActualWidth / 2.0)),
                top.Y + (knob?.Y ?? (float)(travel * (1.0 - fraction)))));
        }

        // The zero line the faders are drawn around, so a fill has something real
        // to start and stop at. Taken from the faders' own geometry, so it cannot
        // drift from them at any band count or window size.
        AudioSlider first = _bandSliders[0];
        double bandTop = first.TranslatePoint(new Point(0, 0), canvas).Y;
        double bandBottom = bandTop + first.ActualHeight;
        double zeroY = (bandTop + bandBottom) / 2.0;

        bool anyAbove = false;
        bool anyBelow = false;
        foreach (Point point in points)
        {
            anyAbove |= point.Y < zeroY - 0.5;
            anyBelow |= point.Y > zeroY + 0.5;
        }

        // The fill first, so the line sits on top of it. Both the shape and the
        // fade live in EqFill, because this is the part of the equaliser that is
        // hardest to judge by reading it.
        if (EqFill.BuildOutline(points, bandTop, bandBottom, zeroY) is PathGeometry fill)
        {
            // Only the geometry and the brush are frozen. A Shape is a
            // FrameworkElement, which is not a Freezable, so freezing the element
            // itself is not an option.
            canvas.Children.Add(new Path
            {
                Data = fill,
                Fill = EqFill.BuildBrush(bandTop, bandBottom),
                Stroke = Brushes.Transparent,
                IsHitTestVisible = false
            });
        }

        PathGeometry line = new();
        Figure lineFigure = new() { IsClosed = false, IsFilled = false };
        lineFigure.StartPoint = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            lineFigure.Segments.Add(new LineSegment(points[i], true));
        }

        line.Figures.Add(lineFigure);
        line.Freeze();

        // The line fades from the zero line out towards whichever end of the band
        // area the curve has gone, so the bright end is the end that means
        // something. Measuring it in absolute coordinates keeps the fade anchored
        // to the panel while the bands move. A curve that never leaves the middle
        // still gets a line, just an even one rather than a fade with nothing to
        // fade across.
        Color lineColour = ((SolidColorBrush)FindResource("AccentAudio")).Color;
        double lineFrom = anyBelow && !anyAbove ? zeroY : bandTop;
        double lineTo = anyBelow && !anyAbove ? bandBottom : bandTop;

        if (Math.Abs(lineTo - lineFrom) < 1.0)
        {
            lineTo = lineFrom + 1.0;
        }

        LinearGradientBrush lineBrush = new(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0xFF, lineColour.R, lineColour.G, lineColour.B), 0.0),
                new GradientStop(Color.FromArgb(0x8C, lineColour.R, lineColour.G, lineColour.B), 1.0)
            },
            new Point(0, lineFrom),
            new Point(0, lineTo));
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

    /// <summary>
    /// The ring and dot the engine puts on each band, drawn on the canvas above the
    /// faders rather than left to the faders' thumbs.
    /// <para>
    /// Two reasons it is drawn rather than being a thumb. A vertical Track keeps a
    /// thumb's centre half a thumb inside each end of the track, so a thumb can
    /// never reach the top or the bottom and the curve ended up drawn beside the
    /// knob rather than on it, worst at +12 and -12 where it is easiest to see. And
    /// on its own layer the knob covers the dashed line the way the engine's does,
    /// instead of having a dash drawn across its middle.
    /// </para>
    /// <para>
    /// The ring grows under the mouse, which is what the fader's own thumb used to
    /// do, so the hover feedback is not lost with it.
    /// </para>
    /// </summary>
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
        // The window half, and it is deliberately the whole of the synchronous
        // part. Applying audio starts the engine and runs a child process it then
        // waits on, which costs seconds when the engine is slow to answer, and
        // that used to happen right here on the dispatcher. This is reached from
        // the Apply button, from a slot hotkey, from a game launching and from
        // alt-tabbing into one, so the freeze landed on the app's primary
        // workflow. The engine half is queued below and the window carries on.
        _settings.ActiveAudioPresetId = preset.Id;
        _activeAudioId = preset.Id;
        UpdateSoundLabels(preset);
        UpdatePresetChrome();

        if (_audio.IsInstalled)
        {
            _liveAudioName = preset.Name.ToUpperInvariant();
            SessionState.Current.AudioTouched = true;
            QueueAudioPush(preset.Copy(), SelectedDeviceName());
        }
        else
        {
            UpdateFxBanner();
        }

        Commit();

        // Fires before the engine has finished rather than after, which is the
        // point: the user asked for this tune and the panel is already showing
        // it, so there is nothing to wait for. Whether the engine took it is
        // reported separately by the read-back below.
        if (announce)
        {
            Flash(preset.Name + " applied");
        }
    }

    /// <summary>The newest tune waiting to be pushed to the engine.</summary>
    private AudioPreset? _pendingAudio;

    private string _pendingAudioDevice = string.Empty;

    /// <summary>1 while a push is in flight, so two of them never overlap.</summary>
    private int _audioPushBusy;

    /// <summary>
    /// Hands a tune to the engine off the dispatcher, and reads the result back.
    /// <para>
    /// Only the newest request is kept. Holding a queue would push every tune
    /// ever asked for in order, which is exactly wrong: a user dragging through
    /// presets wants the one they stopped on, not all six they passed. So a
    /// request arriving while a push is running replaces whatever was waiting,
    /// and the worker picks it up on its next turn.
    /// </para>
    /// <para>
    /// The verify runs on the same worker rather than as its own task so the two
    /// cannot interleave: the read has to see the state the push produced, not
    /// one an interleaved push has since changed.
    /// </para>
    /// </summary>
    private void QueueAudioPush(AudioPreset preset, string device)
    {
        _pendingAudio = preset;
        _pendingAudioDevice = device;

        if (Interlocked.Exchange(ref _audioPushBusy, 1) == 1)
        {
            return;
        }

        _ = PumpAudioPushAsync();
    }

    private async Task PumpAudioPushAsync()
    {
        try
        {
            while (_pendingAudio is not null)
            {
                AudioPreset preset = _pendingAudio;
                string device = _pendingAudioDevice;
                _pendingAudio = null;
                _pendingAudioDevice = string.Empty;

                AudioService.ApplyReport report;
                try
                {
                    report = await Task.Run(() =>
                    {
                        _audio.StartEngine();
                        _audio.Apply(preset, device);
                        return _audio.Verify(preset, device);
                    });
                }
                catch (Exception ex)
                {
                    TraceLog.Write("AUDIO PUSH", ex);
                    continue;
                }

                await Dispatcher.InvokeAsync(() => ReportApply(report));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _audioPushBusy, 0);

            // A request that landed between the last null check and the flag
            // coming down would otherwise sit with nobody to pick it up.
            if (_pendingAudio is not null && Interlocked.Exchange(ref _audioPushBusy, 1) == 0)
            {
                _ = PumpAudioPushAsync();
            }
        }
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


    /// <summary>
    /// The bypass: every band flat and every effect silent, or all of it back, and
    /// it acts on its own.
    /// </summary>
    /// <remarks>
    /// Two changes got here, and both were corrections.
    /// <para>
    /// It used to only record the choice and wait for Apply, which is how the faders
    /// behave. That was the wrong call: a bypass is a state rather than a parameter
    /// and every hardware unit switches it immediately, and needing a second button
    /// to hear the change is what made this read as decoration rather than a switch.
    /// </para>
    /// <para>
    /// It used to be the equalizer only, and labelled "EQ". That was the second
    /// wrong call, and the more interesting one. Throwing a switch that said EQ while
    /// Clarity and Bass carried on colouring the sound is a control lying to the
    /// person using it, and a control that looks broken is worse than a vague one.
    /// So it now silences the whole chain, which is what the word on it promises.
    /// </para>
    /// <para>
    /// Not a full apply, though. That would commit the preset file, the master gain
    /// and the output device, so a fader the user had moved but not applied would be
    /// committed by an unrelated click.
    /// </para>
    /// <para>
    /// Nothing is destroyed either way: the bypass only changes the values sent to
    /// the engine, never the ones held in the tune. Every setting comes straight
    /// back, including band moves that have not been applied.
    /// </para>
    /// </remarks>
    private void OnBypassChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // Through BypassToggle, because the switch is labelled BYPASS and the setting
        // is named for what it holds, so the two read opposite ways round. Wired
        // straight through, the app came up with BYPASS showing on while the
        // equaliser was running.
        _settings.EffectsEnabled = BypassToggle.EffectsFromSwitch(BypassBox.IsChecked == true);
        _audio.EffectsEnabled = _settings.EffectsEnabled;
        Commit();

        if (_audio.IsInstalled)
        {
            QueueBypassPush(_workAudio.Copy());
        }
        else
        {
            UpdateFxBanner();
        }
    }

    /// <summary>The newest bypass state waiting to go to the engine.</summary>
    private AudioPreset? _pendingBypassPush;

    /// <summary>1 while a bypass push is in flight, so two of them never overlap.</summary>
    private int _bypassPushBusy;

    /// <summary>
    /// Hands the bypass to the engine off the dispatcher, newest state only.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="QueueAudioPush"/>: a user flicking the switch
    /// should hear where they ended up, not work through every position they
    /// passed on the way. The push is a single child process and the read-back
    /// that confirms it can take a second or two, so none of it belongs on the
    /// thread that is drawing the switch.
    /// </remarks>
    private void QueueBypassPush(AudioPreset preset)
    {
        _pendingBypassPush = preset;

        if (Interlocked.Exchange(ref _bypassPushBusy, 1) == 1)
        {
            return;
        }

        _ = PumpBypassPushAsync();
    }

    private async Task PumpBypassPushAsync()
    {
        try
        {
            while (_pendingBypassPush is not null)
            {
                AudioPreset preset = _pendingBypassPush;
                _pendingBypassPush = null;

                AudioService.ApplyReport report;
                try
                {
                    report = await Task.Run(() =>
                    {
                        _audio.PushBypass(preset);
                        return _audio.VerifyBypass(preset);
                    });
                }
                catch (Exception ex)
                {
                    TraceLog.Write("BYPASS PUSH", ex);
                    continue;
                }

                // Nothing at all on success. The switch making the sound change is
                // the whole feedback, and a toast over the top of it would be the
                // app announcing the obvious. Only a failure is worth saying,
                // because then nothing happened and silence would be a lie.
                if (report.Outcome != AudioService.ApplyOutcome.Applied)
                {
                    string detail = await Dispatcher.InvokeAsync(() => string.Join("; ", report.Mismatches));
                    await Dispatcher.InvokeAsync(() => Flash("Bypass did not take: " + detail, true));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _bypassPushBusy, 0);

            // A request that landed between the last null check and the flag coming
            // down would otherwise sit with nobody to pick it up.
            if (_pendingBypassPush is not null && Interlocked.Exchange(ref _bypassPushBusy, 1) == 0)
            {
                _ = PumpBypassPushAsync();
            }
        }
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
