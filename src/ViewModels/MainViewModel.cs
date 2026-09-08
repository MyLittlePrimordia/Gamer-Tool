using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using GamerTool.Core;
using GamerTool.Models;

namespace GamerTool.ViewModels;

public enum AppTab
{
    Display,
    Audio,
    Combos,
    Settings
}

public enum HotkeyTargetKind
{
    Display,
    Audio,
    Combo
}

/// <summary>
/// One 10-band EQ slider's UI-facing state: its fixed frequency, the editable
/// gain in dB, and the rainbow zone it belongs to (background swatch colour +
/// plain-English gamer label in the Audio tab). Editing GainDb immediately
/// notifies MainViewModel via GainChanged so the whole 10-band curve gets
/// re-applied live, exactly like a hardware EQ.
/// </summary>
public sealed class BandGainViewModel : ObservableObject
{
    public int FrequencyHz { get; }
    public string FrequencyLabel { get; }
    public string ZoneLabel { get; }
    public string ZoneColorHex { get; }

    private double _gainDb;
    public double GainDb
    {
        get => _gainDb;
        set
        {
            var clamped = Math.Clamp(value, -12.0, 12.0);
            if (SetProperty(ref _gainDb, clamped))
                GainChanged?.Invoke();
        }
    }

    public event Action? GainChanged;

    public BandGainViewModel(int frequencyHz, string zoneLabel, string zoneColorHex, double initialGainDb)
    {
        FrequencyHz = frequencyHz;
        FrequencyLabel = frequencyHz >= 1000 ? $"{frequencyHz / 1000}kHz" : $"{frequencyHz}Hz";
        ZoneLabel = zoneLabel;
        ZoneColorHex = zoneColorHex;
        _gainDb = initialGainDb;
    }
}

/// <summary>
/// The single MVVM brain of GamerTool: preset selection and live slider state
/// for Display and Audio, combo pairing, hotkey re-mapping (with conflict
/// detection), the temporary "Test" countdown, and Settings-tab toggles. The
/// view (MainWindow) binds to this almost entirely declaratively; the one
/// piece it still does in code-behind is repainting the WriteableBitmap
/// preview, driven by subscribing to PreviewRampChanged.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    #region Tab navigation

    private AppTab _currentTab = AppTab.Display;
    public AppTab CurrentTab
    {
        get => _currentTab;
        set => SetProperty(ref _currentTab, value);
    }

    public ICommand NavigateCommand { get; }

    #endregion

    #region Display tab

    public ObservableCollection<DisplayPreset> DisplayPresets { get; } = new();

    private DisplayPreset? _selectedDisplayPreset;
    public DisplayPreset? SelectedDisplayPreset
    {
        get => _selectedDisplayPreset;
        set
        {
            if (!SetProperty(ref _selectedDisplayPreset, value) || value is null)
                return;

            LoadDisplaySlidersFrom(value);
            _settings.ActiveDisplayPresetId = value.Id;
            ApplyDisplayLive();
        }
    }

    private double _gamma = 1.0;
    public double Gamma
    {
        get => _gamma;
        set { if (SetProperty(ref _gamma, Math.Clamp(value, 0.5, 3.0))) ApplyDisplayLive(); }
    }

    private double _contrast = 1.0;
    public double Contrast
    {
        get => _contrast;
        set { if (SetProperty(ref _contrast, Math.Clamp(value, 0.5, 2.0))) ApplyDisplayLive(); }
    }

    private double _shadowLift;
    public double ShadowLift
    {
        get => _shadowLift;
        set { if (SetProperty(ref _shadowLift, Math.Clamp(value, 0.0, 0.5))) ApplyDisplayLive(); }
    }

    private double _brightnessOffset;
    public double BrightnessOffset
    {
        get => _brightnessOffset;
        set { if (SetProperty(ref _brightnessOffset, Math.Clamp(value, -0.25, 0.25))) ApplyDisplayLive(); }
    }

    private double _gainRed = 1.0;
    public double GainRed
    {
        get => _gainRed;
        set { if (SetProperty(ref _gainRed, Math.Clamp(value, 0.0, 1.5))) ApplyDisplayLive(); }
    }

    private double _gainGreen = 1.0;
    public double GainGreen
    {
        get => _gainGreen;
        set { if (SetProperty(ref _gainGreen, Math.Clamp(value, 0.0, 1.5))) ApplyDisplayLive(); }
    }

    private double _gainBlue = 1.0;
    public double GainBlue
    {
        get => _gainBlue;
        set { if (SetProperty(ref _gainBlue, Math.Clamp(value, 0.0, 1.5))) ApplyDisplayLive(); }
    }

    /// <summary>
    /// Raised every time the live display ramp changes, carrying the freshly
    /// computed RAMP so MainWindow.xaml.cs can repaint the WriteableBitmap
    /// preview with the exact same lookup tables that were just sent to the
    /// physical monitor.
    /// </summary>
    public event Action<RAMP>? PreviewRampChanged;

    public ICommand SelectDisplayPresetCommand { get; }
    public ICommand SaveDisplayPresetCommand { get; }
    public ICommand RecordDisplayHotkeyCommand { get; }
    public ICommand TestDisplayPresetCommand { get; }

    private void LoadDisplaySlidersFrom(DisplayPreset preset)
    {
        // Assign backing fields directly for the bulk load so this doesn't
        // fire seven redundant ApplyDisplayLive() calls - one explicit apply
        // happens right after, from the SelectedDisplayPreset setter that
        // triggered this load.
        _gamma = preset.Gamma;
        _contrast = preset.Contrast;
        _shadowLift = preset.ShadowLift;
        _brightnessOffset = preset.BrightnessOffset;
        _gainRed = preset.GainRed;
        _gainGreen = preset.GainGreen;
        _gainBlue = preset.GainBlue;

        OnPropertyChanged(nameof(Gamma));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(ShadowLift));
        OnPropertyChanged(nameof(BrightnessOffset));
        OnPropertyChanged(nameof(GainRed));
        OnPropertyChanged(nameof(GainGreen));
        OnPropertyChanged(nameof(GainBlue));
    }

    private void ApplyDisplayLive()
    {
        var ramp = DisplayManager.Instance.ComputeRamp(
            Gamma, Contrast, ShadowLift, BrightnessOffset, GainRed, GainGreen, GainBlue);

        DisplayManager.Instance.ApplyRamp(ramp);
        PreviewRampChanged?.Invoke(ramp);
    }

    private void ApplyDisplayPresetById(string presetId)
    {
        var preset = DisplayPresets.FirstOrDefault(p => p.Id == presetId);
        if (preset is not null)
            SelectedDisplayPreset = preset;
    }

    private void ExecuteSaveDisplayPreset()
    {
        if (SelectedDisplayPreset is null)
            return;

        if (SelectedDisplayPreset.IsBuiltIn)
        {
            var custom = SelectedDisplayPreset.Clone();
            custom.Id = Guid.NewGuid().ToString("N");
            custom.Name = $"{SelectedDisplayPreset.Name} (Custom)";
            custom.IsBuiltIn = false;
            custom.Gamma = Gamma;
            custom.Contrast = Contrast;
            custom.ShadowLift = ShadowLift;
            custom.BrightnessOffset = BrightnessOffset;
            custom.GainRed = GainRed;
            custom.GainGreen = GainGreen;
            custom.GainBlue = GainBlue;
            custom.HotkeyId = null;
            custom.HotkeyModifiers = 0;
            custom.HotkeyVirtualKey = 0;

            DisplayPresets.Add(custom);
            _settings.DisplayPresets.Add(custom);
            SelectedDisplayPreset = custom;
        }
        else
        {
            SelectedDisplayPreset.Gamma = Gamma;
            SelectedDisplayPreset.Contrast = Contrast;
            SelectedDisplayPreset.ShadowLift = ShadowLift;
            SelectedDisplayPreset.BrightnessOffset = BrightnessOffset;
            SelectedDisplayPreset.GainRed = GainRed;
            SelectedDisplayPreset.GainGreen = GainGreen;
            SelectedDisplayPreset.GainBlue = GainBlue;
        }

        _settings.Save();
        StatusMessage = $"Saved display preset '{SelectedDisplayPreset!.Name}'.";
    }

    #endregion

    #region Audio tab

    public ObservableCollection<AudioPreset> AudioPresets { get; } = new();
    public ObservableCollection<BandGainViewModel> BandGains { get; } = new();

    private AudioPreset? _selectedAudioPreset;
    public AudioPreset? SelectedAudioPreset
    {
        get => _selectedAudioPreset;
        set
        {
            if (!SetProperty(ref _selectedAudioPreset, value) || value is null)
                return;

            LoadBandGainsFrom(value);
            _enableNativeLoudness = value.EnableNativeLoudness;
            OnPropertyChanged(nameof(EnableNativeLoudness));
            _settings.ActiveAudioPresetId = value.Id;
            ApplyAudioLive();
        }
    }

    private bool _enableNativeLoudness;
    public bool EnableNativeLoudness
    {
        get => _enableNativeLoudness;
        set { if (SetProperty(ref _enableNativeLoudness, value)) ApplyAudioLive(); }
    }

    private AudioApplyResult? _lastAudioApplyResult;
    public AudioApplyResult? LastAudioApplyResult
    {
        get => _lastAudioApplyResult;
        private set => SetProperty(ref _lastAudioApplyResult, value);
    }

    public bool IsEqualizerApoDetected => AudioManager.Instance.IsEqualizerApoInstalled();

    public ICommand SelectAudioPresetCommand { get; }
    public ICommand SaveAudioPresetCommand { get; }
    public ICommand RecordAudioHotkeyCommand { get; }
    public ICommand TestAudioPresetCommand { get; }

    private void LoadBandGainsFrom(AudioPreset preset)
    {
        foreach (var band in BandGains)
            band.GainChanged -= OnBandGainChanged;

        BandGains.Clear();

        var zones = BuildZoneMap();
        for (int i = 0; i < AudioManager.BandFrequenciesHz.Length; i++)
        {
            int freq = AudioManager.BandFrequenciesHz[i];
            var (zoneLabel, zoneColor) = zones[freq];
            var band = new BandGainViewModel(freq, zoneLabel, zoneColor, preset.BandGainsDb[i]);
            band.GainChanged += OnBandGainChanged;
            BandGains.Add(band);
        }
    }

    /// <summary>Maps each of the 10 ISO band frequencies onto one of the 6 rainbow gamer-labeled zones from the Phase 1 spec.</summary>
    private static Dictionary<int, (string Label, string ColorHex)> BuildZoneMap()
    {
        return new Dictionary<int, (string, string)>
        {
            [31] = ("Sub-Bass", "#FF4136"),
            [63] = ("Bass", "#FF851B"),
            [125] = ("Bass", "#FF851B"),
            [250] = ("Low-Mids", "#FFDC00"),
            [500] = ("Low-Mids", "#FFDC00"),
            [1000] = ("Mids", "#2ECC40"),
            [2000] = ("Mids", "#2ECC40"),
            [4000] = ("Presence", "#00E5FF"),
            [8000] = ("Presence", "#00E5FF"),
            [16000] = ("Treble", "#B10DC9"),
        };
    }

    private void OnBandGainChanged() => ApplyAudioLive();

    private void ApplyAudioLive()
    {
        var gains = BandGains.Select(b => b.GainDb).ToArray();
        if (gains.Length != AudioManager.BandFrequenciesHz.Length)
            return;

        LastAudioApplyResult = AudioManager.Instance.ApplyPreset(gains, EnableNativeLoudness);
        OnPropertyChanged(nameof(IsEqualizerApoDetected));
    }

    private void ApplyAudioPresetById(string presetId)
    {
        var preset = AudioPresets.FirstOrDefault(p => p.Id == presetId);
        if (preset is not null)
            SelectedAudioPreset = preset;
    }

    private void ExecuteSaveAudioPreset()
    {
        if (SelectedAudioPreset is null)
            return;

        var gains = BandGains.Select(b => b.GainDb).ToArray();

        if (SelectedAudioPreset.IsBuiltIn)
        {
            var custom = SelectedAudioPreset.Clone();
            custom.Id = Guid.NewGuid().ToString("N");
            custom.Name = $"{SelectedAudioPreset.Name} (Custom)";
            custom.IsBuiltIn = false;
            custom.BandGainsDb = gains;
            custom.EnableNativeLoudness = EnableNativeLoudness;
            custom.HotkeyId = null;
            custom.HotkeyModifiers = 0;
            custom.HotkeyVirtualKey = 0;

            AudioPresets.Add(custom);
            _settings.AudioPresets.Add(custom);
            SelectedAudioPreset = custom;
        }
        else
        {
            SelectedAudioPreset.BandGainsDb = gains;
            SelectedAudioPreset.EnableNativeLoudness = EnableNativeLoudness;
        }

        _settings.Save();
        StatusMessage = $"Saved audio preset '{SelectedAudioPreset!.Name}'.";
    }

    #endregion

    #region Combos tab

    public ObservableCollection<ComboPreset> ComboPresets { get; } = new();

    private string _newComboName = string.Empty;
    public string NewComboName
    {
        get => _newComboName;
        set => SetProperty(ref _newComboName, value);
    }

    private DisplayPreset? _newComboDisplayPreset;
    public DisplayPreset? NewComboDisplayPreset
    {
        get => _newComboDisplayPreset;
        set => SetProperty(ref _newComboDisplayPreset, value);
    }

    private AudioPreset? _newComboAudioPreset;
    public AudioPreset? NewComboAudioPreset
    {
        get => _newComboAudioPreset;
        set => SetProperty(ref _newComboAudioPreset, value);
    }

    public ICommand CreateComboCommand { get; }
    public ICommand DeleteComboCommand { get; }
    public ICommand RecordComboHotkeyCommand { get; }
    public ICommand ActivateComboCommand { get; }

    private void ExecuteCreateCombo()
    {
        if (NewComboDisplayPreset is null || NewComboAudioPreset is null || string.IsNullOrWhiteSpace(NewComboName))
        {
            StatusMessage = "Choose a name, a Display preset, and an Audio preset to create a Combo.";
            return;
        }

        var combo = new ComboPreset
        {
            Name = NewComboName.Trim(),
            DisplayPresetId = NewComboDisplayPreset.Id,
            AudioPresetId = NewComboAudioPreset.Id,
            DisplayPresetName = NewComboDisplayPreset.Name,
            AudioPresetName = NewComboAudioPreset.Name
        };

        ComboPresets.Add(combo);
        _settings.ComboPresets.Add(combo);
        _settings.Save();

        NewComboName = string.Empty;
        StatusMessage = $"Created combo '{combo.Name}'.";
    }

    private void ExecuteDeleteCombo(ComboPreset? combo)
    {
        if (combo is null)
            return;

        if (combo.HotkeyId.HasValue)
            HotkeyManager.Instance.UnregisterHotkey(combo.HotkeyId.Value);

        ComboPresets.Remove(combo);
        _settings.ComboPresets.RemoveAll(c => c.Id == combo.Id);
        _settings.Save();
    }

    public void ActivateCombo(ComboPreset combo)
    {
        ApplyDisplayPresetById(combo.DisplayPresetId);
        ApplyAudioPresetById(combo.AudioPresetId);
        StatusMessage = $"Activated combo '{combo.Name}'.";
    }

    #endregion

    #region Settings tab

    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set
        {
            var previous = _runOnStartup;
            if (!SetProperty(ref _runOnStartup, value))
                return;

            bool succeeded = StartupManager.SetEnabled(value);
            if (!succeeded)
            {
                // Revert the UI toggle if the registry write actually failed
                // (e.g. restricted permissions) instead of showing a switch
                // state that doesn't reflect reality.
                _runOnStartup = previous;
                OnPropertyChanged(nameof(RunOnStartup));
                StatusMessage = "Could not update the Windows startup entry.";
                return;
            }

            _settings.RunOnStartup = value;
            _settings.Save();
        }
    }

    public string PanicHotkeyLabel => "Ctrl + Alt + R";

    public ICommand PanicResetCommand { get; }

    private void ExecutePanicReset()
    {
        DisplayManager.Instance.RestoreFactoryGamma();
        AudioManager.Instance.ClearEqualizerApoBands();
        AudioManager.Instance.TrySetNativeLoudnessEqualization(false);
        StatusMessage = "Emergency reset applied: factory display and audio settings restored.";
    }

    #endregion

    #region Hotkey recording

    private bool _isRecordingHotkey;
    public bool IsRecordingHotkey
    {
        get => _isRecordingHotkey;
        private set => SetProperty(ref _isRecordingHotkey, value);
    }

    private string _recordingTargetLabel = string.Empty;
    public string RecordingTargetLabel
    {
        get => _recordingTargetLabel;
        private set => SetProperty(ref _recordingTargetLabel, value);
    }

    private object? _recordingTarget;
    private HotkeyTargetKind _recordingKind;

    public void BeginHotkeyRecording(object target, HotkeyTargetKind kind, string label)
    {
        _recordingTarget = target;
        _recordingKind = kind;
        RecordingTargetLabel = label;
        IsRecordingHotkey = true;
        StatusMessage = $"Press a new key combination for '{label}'...";
    }

    public void CancelHotkeyRecording()
    {
        IsRecordingHotkey = false;
        _recordingTarget = null;
        StatusMessage = "Hotkey recording cancelled.";
    }

    /// <summary>
    /// Called by MainWindow.xaml.cs from a PreviewKeyDown handler while
    /// IsRecordingHotkey is true. Requires at least one modifier so an
    /// accidental single-key press never gets bound as a global hotkey.
    /// </summary>
    public void CaptureHotkeyInput(ModifierKeys modifiers, Key key)
    {
        if (!IsRecordingHotkey || _recordingTarget is null)
            return;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return; // wait for a real, non-modifier key
        }

        if (modifiers == ModifierKeys.None)
        {
            StatusMessage = "Hotkeys must include at least one modifier (Ctrl, Alt, Shift, or Win).";
            return;
        }

        uint win32Modifiers = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) win32Modifiers |= User32Native.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) win32Modifiers |= User32Native.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) win32Modifiers |= User32Native.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) win32Modifiers |= User32Native.MOD_WIN;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        // If the target already owns a hotkey, release its current claim
        // first so re-typing the exact same combination it already holds is
        // treated as success rather than a false "already in use" conflict.
        int? existingId = GetExistingHotkeyId(_recordingTarget, _recordingKind);
        if (existingId.HasValue)
            HotkeyManager.Instance.UnregisterHotkey(existingId.Value);

        if (!HotkeyManager.Instance.IsCombinationAvailable(win32Modifiers, virtualKey))
        {
            // Re-claim the old binding since the new one didn't work out, so
            // the preset isn't left with no working hotkey at all.
            if (existingId.HasValue)
                ReclaimPreviousHotkey(_recordingTarget, _recordingKind, existingId.Value);

            StatusMessage = "That combination is already in use by another application. Try a different one.";
            return;
        }

        Action action = BuildHotkeyAction(_recordingTarget, _recordingKind);
        int newId = HotkeyManager.Instance.RegisterHotkey(win32Modifiers, virtualKey, action, RecordingTargetLabel);

        ApplyHotkeyBindingToTarget(_recordingTarget, _recordingKind, newId, win32Modifiers, virtualKey);

        _settings.Save();
        IsRecordingHotkey = false;
        _recordingTarget = null;
        StatusMessage = $"Bound '{RecordingTargetLabel}' to a new hotkey.";
    }

    private static int? GetExistingHotkeyId(object target, HotkeyTargetKind kind) => kind switch
    {
        HotkeyTargetKind.Display => ((DisplayPreset)target).HotkeyId,
        HotkeyTargetKind.Audio => ((AudioPreset)target).HotkeyId,
        HotkeyTargetKind.Combo => ((ComboPreset)target).HotkeyId,
        _ => null
    };

    private static void ApplyHotkeyBindingToTarget(object target, HotkeyTargetKind kind, int id, uint modifiers, uint virtualKey)
    {
        switch (kind)
        {
            case HotkeyTargetKind.Display:
                var dp = (DisplayPreset)target;
                dp.HotkeyId = id; dp.HotkeyModifiers = modifiers; dp.HotkeyVirtualKey = virtualKey;
                break;
            case HotkeyTargetKind.Audio:
                var ap = (AudioPreset)target;
                ap.HotkeyId = id; ap.HotkeyModifiers = modifiers; ap.HotkeyVirtualKey = virtualKey;
                break;
            case HotkeyTargetKind.Combo:
                var cp = (ComboPreset)target;
                cp.HotkeyId = id; cp.HotkeyModifiers = modifiers; cp.HotkeyVirtualKey = virtualKey;
                break;
        }
    }

    private void ReclaimPreviousHotkey(object target, HotkeyTargetKind kind, int id)
    {
        uint mods = kind switch
        {
            HotkeyTargetKind.Display => ((DisplayPreset)target).HotkeyModifiers,
            HotkeyTargetKind.Audio => ((AudioPreset)target).HotkeyModifiers,
            HotkeyTargetKind.Combo => ((ComboPreset)target).HotkeyModifiers,
            _ => 0u
        };
        uint vk = kind switch
        {
            HotkeyTargetKind.Display => ((DisplayPreset)target).HotkeyVirtualKey,
            HotkeyTargetKind.Audio => ((AudioPreset)target).HotkeyVirtualKey,
            HotkeyTargetKind.Combo => ((ComboPreset)target).HotkeyVirtualKey,
            _ => 0u
        };

        if (mods == 0 || vk == 0)
            return;

        try
        {
            HotkeyManager.Instance.RegisterHotkeyWithId(id, mods, vk, BuildHotkeyAction(target, kind), RecordingTargetLabel);
        }
        catch (HotkeyConflictException)
        {
            // Extremely unlikely (we just unregistered this exact id/combo
            // ourselves moments ago), but a rollback path must never throw.
        }
    }

    private Action BuildHotkeyAction(object target, HotkeyTargetKind kind) => kind switch
    {
        HotkeyTargetKind.Display => () => ApplyDisplayPresetById(((DisplayPreset)target).Id),
        HotkeyTargetKind.Audio => () => ApplyAudioPresetById(((AudioPreset)target).Id),
        HotkeyTargetKind.Combo => () => ActivateCombo((ComboPreset)target),
        _ => () => { }
    };

    #endregion

    #region Test mode countdown

    private DispatcherTimer? _testModeTimer;
    private string? _testModeRevertDisplayId;
    private string? _testModeRevertAudioId;

    private bool _isTestModeActive;
    public bool IsTestModeActive
    {
        get => _isTestModeActive;
        private set => SetProperty(ref _isTestModeActive, value);
    }

    private int _testModeSecondsRemaining;
    public int TestModeSecondsRemaining
    {
        get => _testModeSecondsRemaining;
        private set => SetProperty(ref _testModeSecondsRemaining, value);
    }

    private const int TestModeDurationSeconds = 5;

    private void ExecuteTestDisplayPreset(DisplayPreset? preset)
    {
        if (preset is null || IsTestModeActive)
            return;

        _testModeRevertDisplayId = SelectedDisplayPreset?.Id;
        ApplyDisplayPresetById(preset.Id);
        StartTestModeCountdown(revertDisplay: true, revertAudio: false);
    }

    private void ExecuteTestAudioPreset(AudioPreset? preset)
    {
        if (preset is null || IsTestModeActive)
            return;

        _testModeRevertAudioId = SelectedAudioPreset?.Id;
        ApplyAudioPresetById(preset.Id);
        StartTestModeCountdown(revertDisplay: false, revertAudio: true);
    }

    private void StartTestModeCountdown(bool revertDisplay, bool revertAudio)
    {
        IsTestModeActive = true;
        TestModeSecondsRemaining = TestModeDurationSeconds;
        StatusMessage = $"Testing preset - reverting in {TestModeSecondsRemaining}s...";

        _testModeTimer?.Stop();
        _testModeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _testModeTimer.Tick += (_, _) => OnTestModeTick(revertDisplay, revertAudio);
        _testModeTimer.Start();
    }

    private void OnTestModeTick(bool revertDisplay, bool revertAudio)
    {
        TestModeSecondsRemaining--;

        if (TestModeSecondsRemaining > 0)
        {
            StatusMessage = $"Testing preset - reverting in {TestModeSecondsRemaining}s...";
            return;
        }

        _testModeTimer?.Stop();
        _testModeTimer = null;
        IsTestModeActive = false;

        if (revertDisplay && _testModeRevertDisplayId is not null)
            ApplyDisplayPresetById(_testModeRevertDisplayId);

        if (revertAudio && _testModeRevertAudioId is not null)
            ApplyAudioPresetById(_testModeRevertAudioId);

        StatusMessage = "Test complete - reverted to your previous preset.";
    }

    #endregion

    #region Status

    private string _statusMessage = "Ready.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;

        NavigateCommand = new RelayCommand<AppTab>(tab => CurrentTab = tab);

        SelectDisplayPresetCommand = new RelayCommand<DisplayPreset>(p => { if (p is not null) SelectedDisplayPreset = p; });
        SaveDisplayPresetCommand = new RelayCommand(ExecuteSaveDisplayPreset);
        RecordDisplayHotkeyCommand = new RelayCommand<DisplayPreset>(p => { if (p is not null) BeginHotkeyRecording(p, HotkeyTargetKind.Display, p.Name); });
        TestDisplayPresetCommand = new RelayCommand<DisplayPreset>(ExecuteTestDisplayPreset);

        SelectAudioPresetCommand = new RelayCommand<AudioPreset>(p => { if (p is not null) SelectedAudioPreset = p; });
        SaveAudioPresetCommand = new RelayCommand(ExecuteSaveAudioPreset);
        RecordAudioHotkeyCommand = new RelayCommand<AudioPreset>(p => { if (p is not null) BeginHotkeyRecording(p, HotkeyTargetKind.Audio, p.Name); });
        TestAudioPresetCommand = new RelayCommand<AudioPreset>(ExecuteTestAudioPreset);

        CreateComboCommand = new RelayCommand(ExecuteCreateCombo);
        DeleteComboCommand = new RelayCommand<ComboPreset>(ExecuteDeleteCombo);
        RecordComboHotkeyCommand = new RelayCommand<ComboPreset>(c => { if (c is not null) BeginHotkeyRecording(c, HotkeyTargetKind.Combo, c.Name); });
        ActivateComboCommand = new RelayCommand<ComboPreset>(c => { if (c is not null) ActivateCombo(c); });

        PanicResetCommand = new RelayCommand(ExecutePanicReset);

        foreach (var preset in _settings.DisplayPresets)
            DisplayPresets.Add(preset);
        foreach (var preset in _settings.AudioPresets)
            AudioPresets.Add(preset);
        foreach (var combo in _settings.ComboPresets)
            ComboPresets.Add(combo);

        // Registry is ground truth for "Run on Startup" - a stale
        // settings.json (e.g. the user removed the Run key by hand) should
        // never leave the toggle showing a state that isn't real.
        _runOnStartup = StartupManager.IsEnabled();
        _settings.RunOnStartup = _runOnStartup;

        var initialDisplay = DisplayPresets.FirstOrDefault(p => p.Id == _settings.ActiveDisplayPresetId)
                              ?? DisplayPresets.FirstOrDefault();
        var initialAudio = AudioPresets.FirstOrDefault(p => p.Id == _settings.ActiveAudioPresetId)
                            ?? AudioPresets.FirstOrDefault();

        if (initialDisplay is not null)
            SelectedDisplayPreset = initialDisplay;
        if (initialAudio is not null)
            SelectedAudioPreset = initialAudio;
    }

    /// <summary>
    /// Re-registers every hotkey persisted in settings.json with
    /// HotkeyManager. Called once from App.xaml.cs after both
    /// HotkeyManager.Initialize() and this ViewModel have been constructed,
    /// so saved bindings from a previous session keep working immediately
    /// without the user needing to re-type them.
    /// </summary>
    public void RestoreSavedHotkeys()
    {
        foreach (var preset in DisplayPresets.Where(p => p.HotkeyId.HasValue))
            TryRestoreHotkey(preset.HotkeyId!.Value, preset.HotkeyModifiers, preset.HotkeyVirtualKey,
                BuildHotkeyAction(preset, HotkeyTargetKind.Display), preset.Name);

        foreach (var preset in AudioPresets.Where(p => p.HotkeyId.HasValue))
            TryRestoreHotkey(preset.HotkeyId!.Value, preset.HotkeyModifiers, preset.HotkeyVirtualKey,
                BuildHotkeyAction(preset, HotkeyTargetKind.Audio), preset.Name);

        foreach (var combo in ComboPresets.Where(c => c.HotkeyId.HasValue))
            TryRestoreHotkey(combo.HotkeyId!.Value, combo.HotkeyModifiers, combo.HotkeyVirtualKey,
                BuildHotkeyAction(combo, HotkeyTargetKind.Combo), combo.Name);
    }

    private static void TryRestoreHotkey(int id, uint modifiers, uint virtualKey, Action action, string label)
    {
        if (modifiers == 0 || virtualKey == 0)
            return;

        try
        {
            HotkeyManager.Instance.RegisterHotkeyWithId(id, modifiers, virtualKey, action, label);
        }
        catch (HotkeyConflictException)
        {
            // Another application has since claimed this combination (e.g.
            // it changed since last session). The preset simply stays
            // unbound until the user re-records it from the UI - this must
            // never crash startup.
        }
    }
}
