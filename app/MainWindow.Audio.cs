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
        List<DeviceChoice> choices = new() { new DeviceChoice { Id = string.Empty, Name = DeviceChoice.SystemDefaultName } };

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

    /// <summary>
    /// Fills the device picker, with the engine read moved off the dispatcher.
    /// <para>
    /// Working out the output list means asking the engine, which starts a
    /// process and waits on it and then polls its state file - hundreds of
    /// milliseconds at best. That is not something to do on the thread that
    /// paints the window, and this used to: a rescan did it inline, right after
    /// invalidating the cache that guarantees the slow path is the one taken, and
    /// a backup restore did the same. Both froze the window mid-click.
    /// </para>
    /// <para>
    /// Same shape as the startup path, which was already doing this correctly.
    /// </para>
    /// </summary>
    private async Task LoadDevicesAsync()
    {
        try
        {
            List<DeviceChoice> choices = await Task.Run(BuildDeviceChoices);
            LoadDevicesFrom(choices);
        }
        catch (Exception ex)
        {
            // Includes the window closing underneath the read.
            TraceLog.Write("DEVICE LIST", ex);
        }
    }

    /// <summary>Fills the picker from a list that was worked out elsewhere.</summary>
    /// <summary>
    /// The output devices the app found, kept rather than handed straight to the
    /// dropdown.
    /// <para>
    /// Held because working them out means reading the engine's device list,
    /// which is the slowest read in the app, and because the slot menu needs to
    /// offer the same list. Building a second one for the slot menu would either
    /// be slow on every right click or be a second copy that can disagree with
    /// the first.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> _knownOutputDevices = Array.Empty<string>();

    private void LoadDevicesFrom(List<DeviceChoice> choices)
    {
        _knownOutputDevices = choices
            .Select(c => c.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();

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

        // The slot rows carry a copy of this list, and they are built when the
        // tab opens - which is before this read has necessarily finished, since
        // it starts at launch and answers slowly. So a row built first would
        // have shown whatever devices existed at that moment and could have
        // shown none at all.
        //
        // Rebuilt only while the tab is actually showing, because rebuilding
        // otherwise would be work nobody can see, and the tab is about to
        // rebuild itself anyway when they switch to it.
        if (IsVisible && WindowState != WindowState.Minimized && Pages.SelectedIndex == 2)
        {
            BuildSlots();
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
    /// picker. Required rather than optional because every caller has it, and the
    /// optional form had a branch that re-enumerated - putting the slow engine
    /// read back on the dispatcher - which no caller could reach, so the
    /// parameter only advertised a mistake nobody was making.
    /// </param>
    private void EnsureUsableAudioOutput(OutputRepair repair, List<DeviceChoice> known)
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

            // Only added if the two enumerations disagreed, which would mean a
            // device appeared in between. The comparison has to match the one
            // LoadDevicesFrom makes or the selection will not land.
            if (!known.Any(c => string.Equals(c.Id, choice.Id, StringComparison.OrdinalIgnoreCase)))
            {
                known.Add(choice);
            }

            LoadDevicesFrom(known);

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
        UpdateFxBanner(_audio.ReadState());
    }


    /// <summary>
    /// As <see cref="UpdateFxBanner()"/>, with the engine state already in hand.
    /// <para>
    /// The polling version passes the state it has just read rather than asking for
    /// another one. Without that overload the four second poll could not refresh the
    /// status line without kicking off a second engine read of its own, and the line
    /// was never refreshed by the poll at all - so one wrong reading at startup was
    /// still on screen at the end of the session.
    /// </para>
    /// </summary>
    private void UpdateFxBanner(FxSoundState? snapshot)
    {
        // The one decision, from the five facts, in FxHealth. The wording lives there
        // rather than here because the tray tooltip needs the same four answers and
        // two copies of this text would drift - which is how the Audio tab ends up
        // saying "Ready" while the tray says something else about the same machine.
        //
        // Three answers rather than two, because status.json is another program's
        // output and a file with no power key in it is not the same statement as a
        // file saying false. Reading it as false is what put "EQ switched off" in
        // front of people whose EQ was on.
        FxHealth.PowerState power = snapshot is null || !snapshot.ReportsPower
            ? FxHealth.PowerState.Unknown
            : snapshot.Power
                ? FxHealth.PowerState.On
                : FxHealth.PowerState.Off;

        FxHealth health = FxHealth.Decide(
            installed: _setup.IsInstalled(_audio),
            running: _audio.IsRunning,
            snapshotPresent: snapshot is not null,
            snapshotFresh: snapshot?.IsFresh == true,
            power: power);

        bool missing = health.HowItIs == FxHealth.State.Missing;

        // The installed version is read from the engine's own snapshot rather
        // than out of the update state. That way it is on screen before anything
        // has been checked at all, and it stays correct on a machine where the
        // check cannot answer.
        //
        // It used to come from the update state alone, and that state only
        // carried a version for one of its four outcomes. So "Up to date" was
        // all this panel could ever say about the version it had just checked,
        // and the number the button went to the network for was thrown away the
        // moment it turned out nothing was newer.
        string installed = snapshot is null || snapshot.Version.Length == 0
            ? string.Empty
            : snapshot.Version;

        FxStateText.Text = health.HowItIs == FxHealth.State.Ok
            ? "Ready"
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                health.Caption.ToLowerInvariant());

        // Three colours, because there are three different things being said and two
        // colours cannot carry them. Green is the one answer that means the app
        // knows what the engine is doing. Amber is reserved for states where
        // something is actually wrong and a person has to go and look - missing,
        // not running, not answering.
        //
        // PoweredOff used to be amber, and that was the wrong call. It is not a
        // fault: the user switched the EQ off on purpose and applying any preset
        // switches it back on by itself, so there is nothing to go and fix. In
        // amber it read as a broken install - reported as exactly that, by
        // somebody who could apply a preset and watch it fix itself.
        FxStateText.Foreground = (Brush)FindResource(
            health.IsHealthy
                ? "Green"
                : health.NeedsAttention
                    ? "Amber"
                    : "TextMid");

        if (missing)
        {
            FxPathText.Text = "FxSound is not on this PC yet";
            FxVersionText.Text = "Every audio control on the Audio tab needs it";
            FxInstallButton.Visibility = Visibility.Visible;
            FxStartEngineButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            // The path is still shown when something is wrong with the engine,
            // because "FxSound is installed and running but not answering" is a
            // question somebody will want to answer with the path.
            FxPathText.Text = _audio.ExePath;

            string update = _fxUpdate switch
            {
                { Outcome: FxUpdateOutcome.NewerAvailable } found => "Version " + found.Version + " is out",
                { Outcome: FxUpdateOutcome.UpToDate } => "up to date",
                { Outcome: FxUpdateOutcome.NoChecker } => "winget not on this PC",
                { Outcome: FxUpdateOutcome.Failed } => "update check failed",
                _ => "update not checked",
            };

            // The engine's own complaint still outranks the update note, because it
            // is the more urgent of the two, but it no longer replaces the line. It
            // is appended to it, so a version is on screen in every state.
            FxVersionText.Text = (installed.Length > 0 ? "Installed " + installed : "Installed") + "  ·  "
                + (health.Detail.Length > 0 ? health.Detail : update);

            FxInstallButton.Visibility = Visibility.Collapsed;

            // Only offered when starting it would actually help. It used to be
            // shown whenever FxSound was installed, including while the engine was
            // running perfectly - a button to start something that is already
            // running is a button that confuses people who press it.
            FxStartEngineButton.Visibility = health.OffersStart
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // The button does two jobs rather than appearing and disappearing. Until
        // the user asks, it offers to check; once it knows, it offers the update
        // itself, and then the status line reports the answer.
        //
        // It used to be hidden until an update had already been found, and the
        // finding happened on every launch. Removing the automatic check without
        // changing this would have left a button that could never appear - a
        // control that exists in the markup and is unreachable in the app, which
        // is the exact thing this project is trying not to ship.
        FxUpgradeButton.Visibility = missing ? Visibility.Collapsed : Visibility.Visible;
        FxUpgradeLabel.Text = _fxUpdate switch
        {
            { Outcome: FxUpdateOutcome.NewerAvailable } found when found.Version.Length > 0 => "Update to " + found.Version,
            { Outcome: FxUpdateOutcome.UpToDate } => "Up to date",
            // Not "Check update": there is nothing here that can answer the
            // question, so pressing it would run the same unanswered check again.
            // It says what it is going to do instead.
            { Outcome: FxUpdateOutcome.NoChecker } => "Update",
            { Outcome: FxUpdateOutcome.Failed } => "Try again",
            _ => "Check update",
        };

        // Labelled by its label rather than its content. Setting Content to a
        // string replaces the styled panel inside the button, so the emoji and the
        // button's own padding went with it the moment an update was found.
        FxUpgradeButton.ToolTip = _fxUpdate switch
        {
            { Outcome: FxUpdateOutcome.NewerAvailable } found => "Install FxSound " + found.Version,
            { Outcome: FxUpdateOutcome.UpToDate } => installed.Length > 0
                ? "FxSound " + installed + " is installed and winget reported nothing newer"
                : "Already looked; winget reported nothing newer",
            { Outcome: FxUpdateOutcome.NoChecker } => "winget is not installed, so there is nothing to check against. This downloads the current release directly.",
            { Outcome: FxUpdateOutcome.Failed } => "The last check did not answer. Press to ask again.",
            _ => "Ask winget whether a newer FxSound exists. This is the only thing in the app that uses the network.",
        };

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
        QueueLiveTune();
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
        QueueLiveTune();
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

        // Adding or removing a band is as much a change to the sound as moving one,
        // and the faders it has just built are the only way the user made it. It is
        // also a discrete choice rather than a drag, so it goes straight out rather
        // than waiting on the throttle - there is nothing to coalesce.
        PushLiveTune();
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

        // Pushed here rather than waiting for a button that no longer exists.
        //
        // Choosing a preset and pushing it are one gesture now, the way they always
        // read as one gesture, and going through the full apply path is what keeps
        // this correct: it records the identity in the profile, commits it, and
        // says so. A bare live push would move the sliders without the profile ever
        // learning which tune is loaded, so the next launch would come back on the
        // old one and the change would look like it had been lost.
        ApplyAudio(preset, announce: true);
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


    private void ApplyAudio(AudioPreset preset, bool announce) =>
        ApplyAudioCore(preset, announce, null);


    /// <summary>
    /// How often a live slider change is allowed to reach the engine.
    /// <para>
    /// FxSound takes every change as a process that is spawned and waited on -
    /// measured at 77 ms sustained over forty writes in a row, so about thirteen a
    /// second is the hard ceiling. A slider drag fires sixty to a hundred events in
    /// that time, so the throttle is not about the engine's capacity for state
    /// changes; it is about not asking. At 150 ms the user hears each change about
    /// a tenth of a second after making it, which is below the threshold where a
    /// control feels disconnected from its sound.
    /// </para>
    /// </summary>
    private const int LiveTuneIntervalMs = 150;

    private System.Windows.Threading.DispatcherTimer? _liveTuneTimer;
    private bool _liveTuneDirty;


    /// <summary>
    /// Asks for the working tune to be sent to the engine soon.
    /// <para>
    /// Called on every slider and fader move, which is far too often to send
    /// anything. The timer does the sending; this only marks that something
    /// changed, so a burst of moves collapses into one write carrying the final
    /// position rather than a write per pixel of travel.
    /// </para>
    /// <para>
    /// Collapsing is safe because of what sits behind it. The push queue is a
    /// newest-wins queue: a request that has not started yet is replaced by the
    /// next one, so even a change that arrives mid-write ends up on the engine
    /// without two of them interleaving.
    /// </para>
    /// </summary>
    private void QueueLiveTune()
    {
        if (!_ready || _updating || !_audio.IsInstalled)
        {
            return;
        }

        _liveTuneDirty = true;

        _liveTuneTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(LiveTuneIntervalMs)
        };

        _liveTuneTimer.Tick -= OnLiveTuneTick;
        _liveTuneTimer.Tick += OnLiveTuneTick;

        // Started, not restarted. Restarting on every event would mean a
        // continuous drag never reached the engine at all until the finger stopped,
        // which is the exact opposite of what this is for.
        _liveTuneTimer.Start();
    }


    private void OnLiveTuneTick(object? sender, EventArgs e)
    {
        if (!_liveTuneDirty)
        {
            // Nothing changed since the last one went out, so this tick has
            // delivered the last of it and the timer can stop.
            if (_liveTuneTimer is not null)
            {
                _liveTuneTimer.Stop();
            }

            return;
        }

        _liveTuneDirty = false;
        PushLiveTune();
    }


    /// <summary>
    /// Sends the working tune to the engine without any of the ceremony that goes
    /// with a deliberate action.
    /// <para>
    /// No toast, because a drag produces one of these every 150 ms and a row of
    /// them going up and down the screen is worse than no confirmation at all. The
    /// confirmation is the sound changing.
    /// </para>
    /// <para>
    /// No commit either, and this is the part worth being careful about. The
    /// working tune is a scratch copy: an unnamed tweak deliberately is not written
    /// into the profile, because doing so is what once made an edit look like it had
    /// been thrown away on the next launch. So a drag changes nothing durable and
    /// has nothing to save. Choosing a named preset, which does change the
    /// profile, still goes through <see cref="ApplyAudio"/> and still commits.
    /// </para>
    /// <para>
    /// The labels and chrome are not refreshed because the slider handler has
    /// already refreshed them by the time this runs, and the value it would push
    /// is the one it just recorded.
    /// </para>
    /// </summary>
    private void PushLiveTune()
    {
        if (!_audio.IsInstalled)
        {
            return;
        }

        _liveAudioName = _workAudio.Name.ToUpperInvariant();
        SessionState.Current.AudioTouched = true;
        QueueAudioPush(_workAudio.Copy(), SelectedDeviceName());
    }

    /// <summary>
    /// Applies a tune on behalf of a slot, which may route it somewhere other
    /// than the app's own output.
    /// <para>
    /// A slot is passed rather than a device name because the two have to be
    /// read together: the slot names a device, and whether that name is still
    /// usable is a question about what the app currently knows about. Resolving
    /// it in one place next to the push is what keeps the resolver and the code
    /// that depends on it from drifting apart.
    /// </para>
    /// </summary>
    private void ApplyAudioToDevice(AudioPreset preset, bool announce, HotkeySlot slot) =>
        ApplyAudioCore(preset, announce, slot);

    private void ApplyAudioCore(AudioPreset preset, bool announce, HotkeySlot? routeFrom)
    {
        string device = routeFrom is null
            ? SelectedDeviceName()
            : SlotService.ResolveOutputDevice(routeFrom, _knownOutputDevices, SelectedDeviceName());

        // The window half, and it is deliberately the whole of the synchronous
        // part. Applying audio starts the engine and runs a child process it then
        // waits on, which costs seconds when the engine is slow to answer, and
        // that used to happen right here on the dispatcher. This is reached from
        // the Apply button, from a slot hotkey, from a game launching and from
        // alt-tabbing into one, so the freeze landed on the app's primary
        // workflow. The engine half is queued below and the window carries on.
        //
        // The identity written here is only recorded when the preset actually has
        // an identity. Moving a fader and pressing Apply sends a working tune whose
        // Id is still the preset it was derived from, and recording that made the
        // profile, the tray and the panel all name a preset the engine is not
        // playing - and the next launch reloaded the untouched original, so the
        // edit looked like it had been thrown away. A tune the user has not given
        // a name to is not a preset, and says so.
        bool isNamedPreset = !string.Equals(preset.Id, "flat", StringComparison.OrdinalIgnoreCase)
            && FindAudio(preset.Id) is not null;

        if (isNamedPreset)
        {
            _settings.ActiveAudioPresetId = preset.Id;
            _activeAudioId = preset.Id;
        }

        UpdateSoundLabels(preset);
        UpdatePresetChrome();

        if (_audio.IsInstalled)
        {
            _liveAudioName = preset.Name.ToUpperInvariant();
            SessionState.Current.AudioTouched = true;
            QueueAudioPush(preset.Copy(), device);
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

    /// <summary>
    /// What the queue has been asked to do next.
    /// <para>
    /// A reset is a request like any other, and that is the whole fix. It used
    /// to run outside the queue, which meant a panic key and a slot key a moment
    /// apart could interleave: the reset's three engine calls take about half a
    /// second, and the writes it made afterwards - the active preset id, the
    /// profile on disk, the panel - landed after the newer tune had been applied
    /// and overwrote it. The app came back remembering Flat while the engine was
    /// playing something else, and the next launch loaded the wrong preset.
    /// </para>
    /// <para>
    /// Sharing the queue also means sharing its "newest wins" rule for free,
    /// which is the behaviour a reset wants anyway. Somebody who hits panic
    /// twice wants one reset, not two, and somebody who hits panic and then a
    /// slot key wants the slot key - a queue that ran every request in order
    /// would take them back to silence after they had asked for a tune.
    /// </para>
    /// </summary>
    private sealed class AudioPushRequest
    {
        private AudioPushRequest(AudioPreset? preset, string device, Action<AudioService>? action)
        {
            Preset = preset;
            Device = device;
            Action = action;
        }

        /// <summary>The tune to apply, or null when this is not a preset push.</summary>
        public AudioPreset? Preset { get; }

        public string Device { get; }

        /// <summary>
        /// An engine action that has to run in the queue's turn but is not a tune
        /// push: writing a preset into FxSound, or moving the output.
        /// </summary>
        public Action<AudioService>? Action { get; }

        public bool IsReset => Preset is null && Action is null;

        /// <summary>
        /// The queue's request count when this one was enqueued, so a reset can
        /// tell whether anything has been asked for since. See
        /// <see cref="MainWindow.NextAudioRequestSerial"/>.
        /// </summary>
        public int Serial { get; set; }

        public static AudioPushRequest ForPreset(AudioPreset preset, string device) => new(preset, device, null);

        public static AudioPushRequest ForReset() => new(null, string.Empty, null);

        public static AudioPushRequest ForExclusive(Action<AudioService> action) => new(null, string.Empty, action);
    }

    /// <summary>
    /// Every call into the engine goes through this, in one at a time.
    /// <para>
    /// Built here rather than as a field so the run delegate can reach the
    /// instance methods it dispatches to. Nothing about the queue is specific to
    /// this window, which is why the ordering itself lives in
    /// <see cref="LatestWinsQueue{T}"/> and can be tested without one.
    /// </para>
    /// </summary>
    private LatestWinsQueue<AudioPushRequest> AudioQueue => _audioQueue ??= new LatestWinsQueue<AudioPushRequest>(
        RunAudioRequestAsync,
        ex => TraceLog.Write("AUDIO QUEUE", ex));

    private LatestWinsQueue<AudioPushRequest>? _audioQueue;

    private void QueueAudioPush(AudioPreset preset, string device) =>
        EnqueueAudio(AudioPushRequest.ForPreset(preset, device));

    private void EnqueueAudio(AudioPushRequest request)
    {
        request.Serial = NextAudioRequestSerial();

        // Only a preset push puts a tune back. Recorded against the serial so the
        // reset's guard can tell "something newer was asked for, and it will
        // restore a tune" from "something newer was asked for, and it will not".
        _audioRestoreSerial = request.Preset is not null ? request.Serial : 0;

        AudioQueue.Enqueue(request);
    }

    /// <summary>
    /// Serial of the newest request that will put a tune back on the engine. Zero
    /// when the newest thing asked for was not a tune.
    /// <para>
    /// The reset's guard reads this, and the distinction is load-bearing. Standing
    /// down is only correct when the newer request restores a tune: the panic key,
    /// then a slot key, and the slot wins - which is the whole reason the guard
    /// exists. But the queue also carries actions that touch the engine without
    /// applying anything, and those restore nothing. Panic key, then change the
    /// output device: the reset flattened the engine, stood down because the
    /// request serial had moved, and left the panel naming a preset that was no
    /// longer playing - with AudioTouched still set, so the exit path ran the reset
    /// a second time.
    /// </para>
    /// <para>
    /// So an action records zero rather than its own serial, and the reset stands
    /// down only for a real tune push. An action that did not change the tune
    /// leaves the reset's own writes standing, which is right: the reset is then
    /// the newest thing that happened to the sound.
    /// </para>
    /// </summary>
    private int _audioRestoreSerial;

    /// <summary>
    /// Takes the next serial. Every request goes through here, so a serial says
    /// both "how many things were asked for" and "which one was this".
    /// </summary>
    private int NextAudioRequestSerial() => Interlocked.Increment(ref _audioRequestSerial);

    /// <summary>
    /// The count of requests handed to the queue.
    /// </summary>
    private int _audioRequestSerial;

    private Task WaitForAudioQueueAsync() => AudioQueue.WhenIdle();

    /// <summary>
    /// Carries out one queued request.
    /// <para>
    /// Every branch ends up off the dispatcher and comes back on it, so the
    /// engine is only ever touched from one place at a time and the state
    /// writes all land on the thread that owns them.
    /// </para>
    /// </summary>
    private async Task RunAudioRequestAsync(AudioPushRequest request)
    {
        if (request.IsReset)
        {
            await RunSoundResetAsync(request.Serial);
            return;
        }

        if (request.Action is { } action)
        {
            // An engine action that is a command sequence of its own but is not a
            // tune push: writing a preset into FxSound, moving the output, or
            // throwing the bypass. It waits its turn for the same reason a reset
            // does, which is what stops two engine invocations overlapping.
            //
            // BypassReport is cleared before the action runs, not after it
            // returns. Clearing afterwards meant a bypass whose PushBypass threw
            // left the previous turn's verdict in the field, and the next
            // unrelated action - a device change, a save into FxSound - then read
            // it and announced a bypass failure that had nothing to do with it.
            BypassReport = null;

            try
            {
                await Task.Run(() => action(_audio));
            }
            catch (Exception ex)
            {
                TraceLog.Write("AUDIO ACTION", ex);
                return;
            }

            if (BypassReport is { } bypass)
            {
                BypassReport = null;
                if (bypass.Outcome != AudioService.ApplyOutcome.Applied)
                {
                    _bypassReports.Enqueue(() =>
                        Flash("Bypass did not take: " + string.Join("; ", bypass.Mismatches), true));
                }
            }

            // With the queue drained, a report queued above is now about the state
            // the engine is actually in rather than one it has already left.
            await Dispatcher.InvokeAsync(DrainBypassReports);
            return;
        }

        AudioService.ApplyReport report;
        try
        {
            AudioPreset preset = request.Preset!;
            string device = request.Device;

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
            return;
        }

        await Dispatcher.InvokeAsync(() => ReportApply(report));
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
        EnqueueAudio(AudioPushRequest.ForReset());
        await WaitForAudioQueueAsync();
    }


    /// <summary>
    /// The engine half of a reset, plus the state that has to follow it.
    /// <para>
    /// Both halves together, and both inside the queue's turn. That fixes the
    /// engine half clobbering a newer tune, and it is why the comment above used
    /// to claim the state half was safe as well.
    /// </para>
    /// <para>
    /// It is not, quite, because a newer tune's own writes are outside the queue
    /// by necessity - they happen at enqueue time so that IsLoaded answers
    /// correctly in the click handler that follows without waiting on a child
    /// process. So the reset stands down if anything has been enqueued since it
    /// was. That newer request is pending by then and is about to apply, which is
    /// what the user asked for; the engine has already been flattened, which the
    /// next request undoes.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task RunSoundResetAsync(int serial)
    {
        // Off the dispatcher first. ResetSoundAsync is three engine calls that
        // each wait on a child process, and this is reached from paths where the
        // user is waiting on the result: a hotkey, the reset button, and now a
        // game closing. The window work after it goes back across explicitly, so
        // the only thing that moves off the UI thread is the blocking part.
        //
        // The process-wide stop token rather than anything owned here, because the
        // thing that has to interrupt this is EmergencyReset, and it reaches the
        // engine from AppDomain.ProcessExit where this window does not exist.
        try
        {
            await System.Threading.Tasks.Task.Run(
                () => _audio.ResetSoundAsync(SessionState.Current.AudioStop));
        }
        catch (Exception ex)
        {
            TraceLog.Write("SOUND RESET", ex);
        }

        // Stood down, so nothing about the window is touched either. Dispatcher
        // work after the engine has already been reset by the exit path would
        // repaint a preset nobody is looking at, save the profile a second time,
        // and - the part that actually matters - post to a dispatcher that is
        // already shutting down, where the await may never come back and the
        // queue's pump would sit unfinished for the rest of the process's life.
        if (SessionState.Current.AudioStop.IsCancellationRequested)
        {
            TraceLog.Write("SOUND RESET stood down: the app is going away");
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_audioRestoreSerial > serial)
                {
                    // A newer tune push arrived while the engine was being
                    // flattened. Its own writes describe what is about to be on
                    // screen, and writing flat over them is what left the engine
                    // playing a slot while the panel and the profile both said flat.
                    //
                    // Compared against the restore serial rather than the request
                    // serial on purpose. A newer *action* - moving the output,
                    // saving a preset into FxSound - is not a tune, so the reset
                    // still stands: standing down for one of those would flatten
                    // the engine and leave the panel naming a preset that is no
                    // longer playing, with the exit path re-running a reset
                    // because AudioTouched was never cleared.
                    TraceLog.Write("SOUND RESET stood down: a newer tune is already queued");
                    return;
                }

                SessionState.Current.AudioTouched = false;
                _liveAudioName = "FLAT";
                _activeAudioId = "flat";

                // Persisted as well as set in memory, for the same reason the screen
                // reset does it. Reached by the Reset button, the panic key, a slot
                // being switched off, and a game exiting, and without this the sound
                // went to Flat on screen while the profile went on remembering the
                // preset that was applied before it.
                _settings.ActiveAudioPresetId = "flat";

                // Read here rather than before the wait, so it is the band count
                // the panel has now rather than the one it had several seconds ago.
                int bands = _bandSliders.Count > 0 ? _bandSliders.Count : AudioPreset.PresetBandCount;
                LoadTune(_workDisplay, AudioPreset.Flat(bands));
                RefreshPresetBoxes();
                UpdateLiveLabels();
                Commit();
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


    private async void OnSaveInFxSoundClick(object sender, RoutedEventArgs e)
    {
        if (!_audio.IsInstalled)
        {
            UpdateFxBanner();
            Flash("Install FxSound from Settings first", true);
            return;
        }

        // Queued, because this is an apply followed by a save, and it used to
        // run both immediately. Landing in the middle of a push meant the curve
        // written into FxSound's own preset list was whichever one the engine
        // happened to be holding, not the one on the panel.
        AudioPreset wanted = _workAudio.Copy();
        string device = SelectedDeviceName();

        EnqueueAudio(AudioPushRequest.ForExclusive(audio =>
        {
            audio.Apply(wanted, device);
            audio.SavePresetInFxSound("Gamer Tool");
        }));

        await WaitForAudioQueueAsync();
        Flash("Saved in FxSound as Gamer Tool");
    }


    private async void OnRescanDevicesClick(object sender, RoutedEventArgs e)
    {
        // Invalidated first so the list is genuinely re-read rather than served
        // from a three second cache, which is what makes this the slowest read in
        // the app. That used to be the argument for doing it inline; it is
        // actually the argument for doing it on a worker.
        _audio.InvalidateCache();
        await LoadDevicesAsync();
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

        // Queued, for the same reason the save is. This is a --output command
        // sequence, and one of those landing in the middle of somebody else's
        // apply either loses the move or, worse, re-points the output after the
        // apply already set it. The setting is written first either way, so the
        // profile is the record of the choice even if the engine has not caught
        // up yet.
        if (_audio.IsInstalled)
        {
            string id = choice.Id;
            EnqueueAudio(AudioPushRequest.ForExclusive(audio => audio.SetOutputDevice(id)));
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
        //
        // A request that arrives while a read is in flight is remembered rather
        // than dropped. Dropping it is what left the settings line three and four
        // seconds stale after somebody pressed FxSound's power button: the watch
        // fired, landed on top of a poll that had already started, and was thrown
        // away, so the only thing left to notice the change was the next tick.
        // The requests that matter most are the ones raised by a person pressing a
        // button in another program, and those are exactly the ones most likely to
        // arrive mid-read.
        if (Interlocked.Exchange(ref _fxStateBusy, 1) == 1)
        {
            Interlocked.Exchange(ref _fxStateQueued, 1);
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

            // Drain the request that arrived while this read was in flight, once the
            // guard is clear. Forced, because it exists to catch a change that just
            // happened and the cached answer would be the one taken before it.
            //
            // Started from the finally rather than after it, and deliberately not
            // awaited: this is the tail of an async method on the dispatcher, and
            // awaiting would mean the read only ever completes once the follow-up
            // read has too.
            if (Interlocked.Exchange(ref _fxStateQueued, 0) == 1)
            {
                _ = RefreshFxStateAsync(true);
            }
        }
    }


    /// <summary>
    /// The window half of a poll, and the only part allowed to touch controls.
    /// Runs on the dispatcher, so nothing in here may block.
    /// </summary>
    private void ApplyEngineState(FxSoundState state)
    {
        // The status line is rebuilt from every poll, not only from the handful of
        // events that used to call it: install, apply, device change, tab change.
        //
        // It was not, and that is half of why a wrong answer could sit on screen
        // indefinitely. The engine's state moves on its own - the user switches the
        // EQ off in FxSound, or the engine finishes starting up and starts reporting
        // - and nothing the app did was watching. A line that describes another
        // program has to be re-read while it is on screen, or it is a snapshot with
        // no indication that it is one.
        UpdateFxBanner(state);

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
    /// The loud guard: how far the engine's volume levelling is lifted before a
    /// tune goes out.
    /// <para>
    /// Acts on its own, like the bypass and for the same reason. It is a state
    /// rather than a parameter, it is not part of any saved tune, and someone who
    /// wants their games held down wants that to hold for whatever they load next.
    /// Making it wait for Apply would mean switching it on and hearing nothing
    /// happen, which is what made the original bypass read as decoration.
    /// </para>
    /// <para>
    /// The re-apply goes through the ordinary path, so it coalesces with anything
    /// else already in flight rather than racing it, and the panel reports the
    /// result the same way it does for any other apply.
    /// </para>
    /// </summary>
    private void OnLoudGuardChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.LoudGuard = LoudGuardBox.IsChecked == true;
        _audio.LoudGuardEnabled = _settings.LoudGuard;
        Commit();

        if (_audio.IsInstalled)
        {
            // The copy, because the working tune is what the panel is showing and
            // the guard is folded in further down, on the way to the engine.
            QueueAudioPush(_workAudio.Copy(), SelectedDeviceName());
        }
        else
        {
            UpdateFxBanner();
        }
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

    /// <summary>
    /// Hands the bypass to the engine, in the queue's turn rather than beside it.
    /// </summary>
    /// <remarks>
    /// This ran on its own pump, with its own in-flight flag, alongside the audio
    /// queue. That is the one thing the audio queue exists to prevent: two engine
    /// operations at once. Every other path into FxSound goes through it, so a flick
    /// of the bypass switch during an apply - one keystroke and one click apart, and
    /// Apply sits right beside the switch - put <c>--set_band_gain</c> in flight at
    /// the same moment as <c>--preset</c>. The engine is one child process per
    /// invocation reading its own arguments, so neither sees the other: a bypass
    /// applied to one tune and a preset applied to the other, with a read-back
    /// reporting drift on both.
    /// <para>
    /// Sharing the queue brings the newest-wins rule with it, which is the
    /// behaviour a switch wants anyway: somebody dragging it should end up where
    /// they let go rather than work through every position on the way.
    /// </para>
    /// </remarks>
    private void QueueBypassPush(AudioPreset preset)
    {
        EnqueueAudio(AudioPushRequest.ForExclusive(audio =>
        {
            audio.PushBypass(preset);
            BypassReport = audio.VerifyBypass(preset);
        }));
    }

    /// <summary>
    /// The read-back from the bypass turn. Written on the pool thread, read on the
    /// dispatcher once the queue has run dry, and cleared before every action so
    /// one turn's verdict cannot be announced against the next.
    /// </summary>
    private AudioService.ApplyReport? BypassReport;

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _bypassReports = new();

    /// <summary>
    /// Reports a bypass turn that did not land, with the queue idle.
    /// <para>
    /// Nothing at all on success. The switch making the sound change is the whole
    /// feedback, and a toast over the top of it would be the app announcing the
    /// obvious. Only a failure is worth saying, because then nothing happened and
    /// silence would be a lie.
    /// </para>
    /// </summary>
    private void DrainBypassReports()
    {
        while (_bypassReports.TryDequeue(out Action? report))
        {
            report();
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
