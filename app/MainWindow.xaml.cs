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
    private readonly ProfileManager _profiles = new();


    private readonly BackupService _backups = new();


    private readonly DisplayPreview _displayPreview = new();


    private readonly AudioPreviewPlayer _audioPreview = new();


    private readonly DisplayService _display = new();


    private readonly AudioService _audio = new();


    private readonly SetupService _setup = new();


    private readonly AudioDeviceManager _devices = new();


    private readonly AppLibraryService _library = new();


    private readonly ProcessWatcherService _watcher = new();


    private readonly HotkeyService _hotkeys = new();


    private readonly StartupService _startup = new();


    private readonly RetroOsd _osd = new();


    private SpectrumView? _spectrum;


    private readonly List<Slider> _bandSliders = new();


    private readonly List<TextBlock> _bandValueBlocks = new();


    private readonly List<TextBlock> _bandLabelBlocks = new();


    /// <summary>Only populated for the band counts whose centre frequencies can move.</summary>
    private readonly List<FrequencyDial> _frequencyDials = new();


    private AppSettings _settings;


    private DisplayPreset _workDisplay = DisplayPreset.Flat();


    private AudioPreset _workAudio = AudioPreset.Flat();


    private List<AppCandidate> _appList = new();


    private DispatcherTimer? _stateTimer;


    private CancellationTokenSourceHolder? _install;


    private bool _updating;


    private bool _ready;


    private bool _suppressDeviceEvents;


    /// <summary>Default playback endpoint name, cached for the audio footer.</summary>
    private string _systemOutput = string.Empty;


    private string? _captureSlotId;


    private TextBox? _captureBox;


    private TrayService? _tray;


    private bool _quitting;


    private string _activeDisplayId = string.Empty;


    private string _activeAudioId = string.Empty;


    private string _liveDisplayName = "NOTHING";


    private string _liveAudioName = "NOTHING";


    /// <summary>Set by the launch-time winget query in SetupService.</summary>
    private bool _fxUpdateAvailable;


    private string _fxUpdateVersion = string.Empty;


    private bool _previewNight;


    private string _autoSlotId = string.Empty;


    private string _autoProcess = string.Empty;


    private DateTime _autoStamp = DateTime.MinValue;


    private bool _previewQueued;


    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };


    public MainWindow()
    {
        InitializeComponent();

        Icon = IconFactory.LoadWindowIcon();

        _settings = _profiles.Load();

        // Settings saved by an older build carry the old defaults, so they are
        // brought up to date before anything reads the active presets.
        if (_settings.Schema < AppSettings.CurrentSchema)
        {
            _settings.Migrate();
            _profiles.Save(_settings);
        }

        _audio.ExePath = _settings.FxSoundPath;
        SessionState.Current.Display = _display;
        SessionState.Current.Audio = _audio;
        _workDisplay = (FindDisplay(_settings.ActiveDisplayPresetId) ?? DisplayPreset.Flat()).Copy();
        _workAudio = (FindAudio(_settings.ActiveAudioPresetId) ?? AudioPreset.Flat()).Copy();
        _activeDisplayId = _workDisplay.Id;
        _activeAudioId = _workAudio.Id;

        BuildBandStrip(AudioPreset.PresetBandCount);
        BandCountBox.ItemsSource = AudioPreset.BandCounts.ToList();
        BandCountBox.SelectedItem = AudioPreset.PresetBandCount;
        BlueLightBox.ItemsSource = DisplayPreset.BlueLightNames.ToList();
        BlueLightBox.SelectedItem = DisplayPreset.BlueLightNames[
            Math.Clamp(_settings.BlueLightFilter, 0, DisplayPreset.BlueLightNames.Length - 1)];
        BuildModal();

        _display.StatusChanged += text => RailStatus.Text = text;
        _audio.StatusChanged += text => RailStatus.Text = text;
        _setup.StatusChanged += text => RailStatus.Text = text;
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Failed += OnHotkeyFailed;
        _watcher.ForegroundChanged += OnForegroundChanged;
        _watcher.TargetLaunched += OnTargetLaunched;
        _watcher.TargetExited += OnTargetExited;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Loaded += OnWindowLoaded;

        // Whatever ends this app, including a crash or a kill, the displays go
        // back to the brightness they were found at.
        SessionState.Current.RestoreBacklight = () => Backlight.RestoreAll();

        GammaLockBox.IsChecked = _settings.GammaLock;
        OsdBox.IsChecked = _settings.ShowOsd;
        AutoSwitchBox.IsChecked = _settings.AutoSwitch;
        AutoRevertBox.IsChecked = _settings.AutoRevertOnExit;
        StartHiddenBox.IsChecked = _settings.StartHidden;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        StartWithWindowsBox.IsChecked = _startup.IsEnabled;
        AntiClipBox.IsChecked = _settings.AntiClip;
        HardwareBrightnessBox.IsChecked = _settings.HardwareBrightnessEnabled;
        _audio.AntiClipEnabled = _settings.AntiClip;
        UpdateAntiClipReadout();

        _tray = new TrayService();
        _tray.ShowRequested += RestoreFromTray;
        _tray.ResetScreenRequested += () => Dispatcher.Invoke(() => _display.Reset());
        _tray.ResetSoundRequested += () => Dispatcher.Invoke(() => _ = _audio.ResetSoundAsync());
        _tray.QuitRequested += QuitApp;

        LoadDevices();
        LoadTune(_workDisplay, _workAudio);
        RefreshPresetBoxes();
        BuildSlots();
        RegisterHotkeys();
        ApplyWatchState();

        UpdatePreviewPills();
        UpdateAudioPreviewState();
        UpdateLiveLabels();
        RenderDisplayPreview();
        _audioPreview.StateChanged += UpdateAudioPreviewState;
        _audioPreview.Failed += text => Flash(text + " failed, check the assets folder", true);
        _audioPreview.Prepare();
        UpdatePreviewTrackButton();

        _spectrum = new SpectrumView(SpectrumHost, () => _audioPreview.Position, () => _audioPreview.IsPlaying);
        _spectrum.SetBandGain(SpectrumBandGain);

        _ready = true;

        SelectPage(StartPage());

        if (StartHidden())
        {
            HideToTray();
        }
    }


    /// <summary>
    /// Gain in dB that one spectrum bar should show, taken from the EQ band whose
    /// centre frequency is nearest. This is what makes the analyser respond as the
    /// faders move rather than just showing the untouched track.
    /// </summary>
    private double SpectrumBandGain(int bar)
    {
        int count = _bandSliders.Count;
        if (count == 0)
        {
            return 0.0;
        }

        double hz = SpectrumView.BarCentre(bar);

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            double distance = Math.Abs(Math.Log(AudioPreset.BandFrequency(count, i) / hz));
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        return Math.Clamp(_workAudio.Band(nearest), AudioPreset.GainMin, AudioPreset.GainMax);
    }


    private static bool StartHidden()
    {
        string[] args = Environment.GetCommandLineArgs();
        foreach (string arg in args)
        {
            if (arg.Equals("--tray", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _tray?.Show();
    }


    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        });
    }


    private void QuitApp()
    {
        _quitting = true;
        Show();
        _audioPreview.Pause();
        EmergencyReset.Run();
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }


    private static int StartPage()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--page", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
                && page >= 0 && page <= 3)
            {
                return page;
            }
        }

        return 0;
    }


    private void SelectPage(int index)
    {
        RadioButton[] nav = { NavDisplay, NavAudio, NavHotkeys, NavSettings };
        foreach (RadioButton item in nav)
        {
            item.IsChecked = ReferenceEquals(item, nav[index]);
        }

        Pages.SelectedIndex = index;

        // The backlight is probed when its own tab is opened, not at launch and
        // not until the option is flipped. That way the I2C bus is never touched
        // before the user has actually gone looking at the thing that needs it,
        // and an option that was left on still works on the next run.
        if (index == 0 && Backlight.IsEnabled && !Backlight.HasProbed)
        {
            _ = Task.Run(() =>
            {
                Backlight.Probe();
                Dispatcher.InvokeAsync(RefreshBacklightRows);
            });
        }
        else if (index == 0)
        {
            RefreshBacklightRows();
        }

        // The analyser only burns CPU while it is actually on screen.
        if (index == 1)
        {
            _spectrum?.Start();
        }
        else
        {
            _spectrum?.Stop();
        }

        if (index == 2)
        {
            BuildSlots();
            _ = WarmAppList();
        }

        if (index != 1 && _audioPreview.IsPlaying)
        {
            _audioPreview.Pause();
        }
    }


    private async System.Threading.Tasks.Task WarmAppList()
    {
        await EnsureAppList();
        if (Pages.SelectedIndex == 2)
        {
            BuildSlots();
        }
    }


    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _display.ScanMonitors();
        _display.StartLock();
        _display.SetLock(_settings.GammaLock);

        UpdateFxBanner();
        LoadDevices();
        EnsureUsableAudioOutput();
        _ = RefreshFxStateAsync(true);

        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _stateTimer.Tick += (s, e) => _ = RefreshFxStateAsync(false);
        _stateTimer.Start();

        if (_settings.StartHidden)
        {
            WindowState = WindowState.Minimized;
        }

        if (_settings.ShowOsd)
        {
            Flash("Ready, hit a slot key to load");
        }

        HotkeySlot? startSlot = _settings.Slots.FirstOrDefault(s => s.Enabled && s.ApplyOnStart && s.HasWork);
        if (startSlot is not null)
        {
            PlaySlot(startSlot, announce: true);
        }

        // The spec line under the preview always shows the real values. It used to
        // be overwritten with "press apply" on a cold start, which is already said
        // by the live state on the right and by the edited dot on the preset tag.
        UpdateLiveLabels();

        // Both FxSound jobs run off the UI thread: the prompt only appears when
        // the engine is genuinely missing, and the update badge is filled in
        // later so a slow winget call never delays the window opening.
        bool missing = !_setup.IsInstalled(_audio);
        if (missing)
        {
            PromptForFxSound();
        }

        _ = CheckFxSoundUpdateAsync();
    }


    /// <summary>
    /// First run on a machine with no FxSound: ask once, then never nag again.
    /// A user who says later can still install from the banner on the Audio tab
    /// or from Settings.
    /// </summary>
    private void PromptForFxSound()
    {
        if (_settings.FxPromptSeen)
        {
            return;
        }

        _settings.FxPromptSeen = true;
        Commit();

        ShowConfirmModal(
            "FXSOUND IS NOT INSTALLED",
            "FxSound does the actual sound work. Without it this app can still change your screen, "
            + "but the audio tab will stay silent. It is a free app and installs in about a minute.",
            "Install FxSound",
            () => _ = InstallAsync(true),
            "Later");
    }


    private async System.Threading.Tasks.Task CheckFxSoundUpdateAsync()
    {
        FxUpdateResult? result = await System.Threading.Tasks.Task.Run(() => _setup.CheckForUpdate());
        if (result is null)
        {
            return;
        }

        _fxUpdateAvailable = result.UpdateAvailable;
        _fxUpdateVersion = result.AvailableVersion;
        UpdateFxBanner();
    }


    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton box || box.Tag is not string tag || !_ready)
        {
            return;
        }

        if (!int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return;
        }

        SelectPage(index);
    }


    // ============ custom title bar ============

    private void OnTitleBarDragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // A second click while already moving; nothing to do.
        }
    }


    private void OnCaptionMinClick(object sender, RoutedEventArgs e)
    {
        // Minimising to the tray rather than the taskbar keeps the hotkeys live
        // and matches what the tray menu does.
        HideToTray();
    }


    private void OnCaptionCloseClick(object sender, RoutedEventArgs e)
    {
        // Close normally and let OnClosing decide what closing means, which is
        // hide-to-tray or shut down depending on the setting. Calling OnClosing
        // straight from here ran the teardown and then let Shutdown raise Closing
        // and run it a second time, on the way out through the non tray path.
        Close();
    }


    private IReadOnlyList<DisplayPreset> AllDisplayPresets()
    {
        List<DisplayPreset> list = new(DisplayPreset.Defaults);
        list.AddRange(_settings.CustomDisplayPresets);
        return list;
    }


    private IReadOnlyList<AudioPreset> AllAudioPresets()
    {
        List<AudioPreset> list = new(AudioPreset.Defaults);
        list.AddRange(_settings.CustomAudioPresets);
        return list;
    }


    private DisplayPreset? FindDisplay(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return AllDisplayPresets().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }


    private AudioPreset? FindAudio(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return AllAudioPresets().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// Rebuilds both preset dropdowns from the built-ins plus anything the user
    /// has saved, and re-selects whichever preset is loaded.
    /// </summary>
    private void RefreshPresetBoxes()
    {
        _updating = true;
        try
        {
            List<PresetChoice> display = new();
            HashSet<string> builtInDisplay = DisplayPreset.Defaults.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayPreset preset in AllDisplayPresets())
            {
                display.Add(new PresetChoice
                {
                    Kind = "display",
                    Id = preset.Id,
                    Name = preset.Name,
                    BuiltIn = builtInDisplay.Contains(preset.Id)
                });
            }

            List<PresetChoice> audio = new();
            HashSet<string> builtInAudio = AudioPreset.Defaults.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (AudioPreset preset in AllAudioPresets())
            {
                audio.Add(new PresetChoice
                {
                    Kind = "audio",
                    Id = preset.Id,
                    Name = preset.Name,
                    BuiltIn = builtInAudio.Contains(preset.Id)
                });
            }

            DisplayPresetBox.ItemsSource = display;
            AudioPresetBox.ItemsSource = audio;
            SelectChoice(DisplayPresetBox, _activeDisplayId);
            SelectChoice(AudioPresetBox, _activeAudioId);
        }
        finally
        {
            _updating = false;
        }

        UpdatePresetChrome();
    }


    private static void SelectChoice(ComboBox box, string id)
    {
        if (box.ItemsSource is not IEnumerable<PresetChoice> choices)
        {
            return;
        }

        foreach (PresetChoice choice in choices)
        {
            if (string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = choice;
                return;
            }
        }
    }


    /// <summary>
    /// Rename and delete only ever act on the user's own presets, so the two
    /// buttons are greyed out unless the loaded one is theirs.
    /// </summary>
    private void UpdatePresetChrome()
    {
        DisplayPreset? display = FindDisplay(_activeDisplayId);
        bool displayMine = _settings.CustomDisplayPresets.Any(p => p.Id == _activeDisplayId);
        DisplayRenameButton.IsEnabled = displayMine;
        DisplayDeleteButton.IsEnabled = displayMine;

        AudioPreset? audio = FindAudio(_activeAudioId);
        bool audioMine = _settings.CustomAudioPresets.Any(p => p.Id == _activeAudioId);
        AudioRenameButton.IsEnabled = audioMine;
        AudioDeleteButton.IsEnabled = audioMine;

        SaveToFxSoundButton.IsEnabled = _audio.IsInstalled;
    }


    private void LoadTune(DisplayPreset display, AudioPreset audio)
    {
        // Copied on the way in. The working tune gets renamed to "Tuned" whenever
        // a slider moves, and FindDisplay and FindAudio hand back the built-in
        // entries themselves, so without a copy that rename would land on the
        // shared list and the shipped presets would start calling themselves
        // "Tuned" for the rest of the session.
        _workDisplay = display.Copy();
        _workAudio = audio.Copy();

        int count = audio.NumBands <= 0 ? AudioPreset.PresetBandCount : audio.NumBands;
        if (_bandSliders.Count != count)
        {
            BuildBandStrip(count);
            BandCountBox.SelectedItem = count;
        }

        _updating = true;
        try
        {
            GammaSlider.Value = display.Gamma;
            ShadowSlider.Value = display.ShadowBoost;
            BrightSlider.Value = display.Brightness;
            ContrastSlider.Value = display.Contrast;
            RedSlider.Value = display.RedGain;
            GreenSlider.Value = display.GreenGain;
            BlueSlider.Value = display.BlueGain;
            ClaritySlider.Value = audio.Clarity;
            AmbienceSlider.Value = audio.Ambience;
            SurroundSlider.Value = audio.Surround;
            DynamicBoostSlider.Value = audio.DynamicBoost;
            BassBoostSlider.Value = audio.BassBoost;
            MasterGainSlider.Value = audio.MasterGain;
            LevelingSlider.Value = audio.VolumeLeveling;
            FilterQSlider.Value = audio.FilterQ;
            BalanceSlider.Value = audio.Balance;
            for (int i = 0; i < _bandSliders.Count; i++)
            {
                _bandSliders[i].Value = audio.Band(i);
            }

            // Put each frequency dial where the loaded tune says it belongs, so a
            // saved tuning comes back with its bands where they were left.
            if (_frequencyDials.Count == count && AudioPreset.HasFrequencyDial(count))
            {
                EnsureFrequencies(audio, count);
                for (int i = 0; i < _frequencyDials.Count; i++)
                {
                    AudioPreset.BandWindow window = AudioPreset.Window(count, i);
                    double step = AudioPreset.StepFromFrequency(window, audio.Frequencies[i]);
                    _frequencyDials[i].SetStep(step);
                    _frequencyDials[i].DefaultStep = AudioPreset.StepFromFrequency(window, AudioPreset.BandFrequency(count, i));
                    if (i < _bandLabelBlocks.Count)
                    {
                        _bandLabelBlocks[i].Text = AudioPreset.FormatFrequency(audio.Frequencies[i]);
                    }
                }
            }
        }
        finally
        {
            _updating = false;
        }

        UpdateScreenLabels(display);
        UpdateSoundLabels(audio);
        UpdatePresetChrome();

        // The curve reads the faders' own geometry, so it can only be drawn once
        // they have been laid out. Waiting for the dispatcher means the numbers are
        // final and ActualHeight is real, instead of guessing a height.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(UpdateEqCurve));
    }


    private static string Signed(double value, string format)
    {
        double rounded = Math.Round(value, 2);
        if (Math.Abs(rounded) < 0.0001)
        {
            return "0";
        }

        string text = rounded.ToString(format, CultureInfo.InvariantCulture);
        return rounded > 0 ? "+" + text : text;
    }


    private void UpdateLiveLabels()
    {
        ScreenLiveText.Text = _liveDisplayName == "NOTHING" ? "Nothing applied" : _liveDisplayName;
        ScreenLiveDot.Fill = _liveDisplayName == "NOTHING"
            ? (Brush)FindResource("TextLow")
            : (Brush)FindResource("AccentDisplay");
    }


    private void Commit()
    {
        _profiles.Save(_settings);
    }


    /// <summary>
    /// One short line of confirmation. The old pair of bracketed headline and
    /// shouted subline was two rows of text for something that only ever needed to
    /// say what just happened, so it is now a single trimmed sentence.
    /// </summary>
    private void Flash(string message)
    {
        if (_settings.ShowOsd)
        {
            _osd.ShowToast(message);
        }
    }


    /// <summary>As <see cref="Flash"/>, but flagged amber for anything that failed.</summary>
    private void Flash(string message, bool warn)
    {
        if (_settings.ShowOsd)
        {
            _osd.ShowToast(message, warn);
        }
    }


    /// <summary>
    /// The quiet half of an apply. The status line says which of the three
    /// outcomes it was, in three words, and the tooltip carries the list of
    /// values the engine did not take. No toast: an apply is a deliberate act,
    /// so it does not need to interrupt, but a mismatch still has to be findable
    /// without turning the sound up and down to hunt for it.
    /// </summary>
    private void ReportApply(AudioService.ApplyReport report)
    {
        string text;
        string? detail;
        bool warn;

        switch (report.Outcome)
        {
            case AudioService.ApplyOutcome.Applied:
                text = "SOUND APPLIED";
                detail = "The engine is doing everything that was asked of it.";
                warn = false;
                break;

            case AudioService.ApplyOutcome.Drifted:
                text = "SOUND DRIFTED";
                detail = "The engine did not take all of it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, report.Mismatches);
                warn = true;
                break;

            default:
                text = "SOUND FAILED";
                detail = report.Mismatches.Count == 0
                    ? "The engine did not report its state."
                    : string.Join(Environment.NewLine, report.Mismatches);
                warn = true;
                break;
        }

        RailStatus.Text = text;
        RailStatus.Foreground = warn
            ? (Brush)FindResource("Amber")
            : (Brush)FindResource("TextMid");
        RailStatus.ToolTip = detail;
    }


    public sealed class DeviceChoice
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public override string ToString()
        {
            return Name;
        }
    }


    public sealed class PresetChoice
    {
        public string Kind { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>True for a preset the app ships, false for one the user saved.</summary>
        public bool BuiltIn { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }


    private sealed class CancellationTokenSourceHolder
    {
        public System.Threading.CancellationTokenSource Source { get; } = new();

        public System.Threading.CancellationToken Token => Source.Token;

        public void Cancel()
        {
            Source.Cancel();
        }
    }

}
