using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
            _isDisplayDirty = false; // fresh load matches the preset
            OnPropertyChanged(nameof(IsDisplayDirty));
            // Preview-only: the screen changes on "Apply to Screen", a preset
            // hotkey, or a combo activation - never from just browsing.
            RefreshDisplayPreview();
            ActiveStateChanged?.Invoke();
        }
    }

    /// <summary>True when slider state diverges from the selected preset - shows Save.</summary>
    private bool _isDisplayDirty;
    public bool IsDisplayDirty
    {
        get => _isDisplayDirty;
        private set => SetProperty(ref _isDisplayDirty, value);
    }

    /// <summary>
    /// Freestyle-style live apply: when ON, slider drags push to the monitor
    /// instantly (plus preview). Default OFF so in-game tweaks never flash the
    /// screen until Apply is pressed - the dummy-proof default.
    /// </summary>
    private bool _isDisplayLive;
    public bool IsDisplayLive
    {
        get => _isDisplayLive;
        set => SetProperty(ref _isDisplayLive, value);
    }

    /// <summary>Hold-to-compare: true while the user holds Bypass - shows factory ramp.</summary>
    private bool _isDisplayBypassed;
    public bool IsDisplayBypassed
    {
        get => _isDisplayBypassed;
        private set => SetProperty(ref _isDisplayBypassed, value);
    }

    private void MarkDisplayDirty()
    {
        if (!IsDisplayDirty)
            IsDisplayDirty = true;
    }

    private double _gamma = 1.0;
    public double Gamma
    {
        get => _gamma;
        set { if (SetProperty(ref _gamma, Math.Clamp(value, 0.5, 3.0))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    private double _contrast = 1.0;
    public double Contrast
    {
        get => _contrast;
        set { if (SetProperty(ref _contrast, Math.Clamp(value, 0.5, 2.0))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    private double _shadowLift;
    public double ShadowLift
    {
        get => _shadowLift;
        set { if (SetProperty(ref _shadowLift, Math.Clamp(value, 0.0, 0.5))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    private double _brightnessOffset;
    public double BrightnessOffset
    {
        get => _brightnessOffset;
        set { if (SetProperty(ref _brightnessOffset, Math.Clamp(value, -0.25, 0.25))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    private double _gainRed = 1.0;
    public double GainRed
    {
        get => _gainRed;
        set { if (SetProperty(ref _gainRed, Math.Clamp(value, 0.0, 1.5))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    private double _gainGreen = 1.0;
    public double GainGreen
    {
        get => _gainGreen;
        set { if (SetProperty(ref _gainGreen, Math.Clamp(value, 0.0, 1.5))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    private double _gainBlue = 1.0;
    public double GainBlue
    {
        get => _gainBlue;
        set { if (SetProperty(ref _gainBlue, Math.Clamp(value, 0.0, 1.5))) { RefreshDisplayPreview(); MarkDisplayDirty(); } }
    }

    /// <summary>
    /// Raised every time the live display ramp changes, carrying the freshly
    /// computed RAMP so MainWindow.xaml.cs can repaint the WriteableBitmap
    /// preview with the exact same lookup tables that were just sent to the
    /// physical monitor.
    /// </summary>
    public event Action<RAMP>? PreviewRampChanged;

    /// <summary>Raised whenever the active display/audio selection changes - App uses it to refresh the tray tooltip/icon.</summary>
    public event Action? ActiveStateChanged;

    public ICommand SelectDisplayPresetCommand { get; }
    public ICommand SaveDisplayPresetCommand { get; }
    public ICommand RecordDisplayHotkeyCommand { get; }
    public ICommand TestDisplayPresetCommand { get; }
    public ICommand CycleDisplayPresetForwardCommand { get; }
    public ICommand CycleDisplayPresetBackCommand { get; }
    public ICommand ApplyDisplayToScreenCommand { get; }
    public ICommand RecordSelectedDisplayHotkeyCommand { get; }
    public ICommand RenameDisplayPresetCommand { get; }
    public ICommand DuplicateDisplayPresetCommand { get; }
    public ICommand DeleteDisplayPresetCommand { get; }
    public ICommand ToggleDisplayFavoriteCommand { get; }
    public ICommand BypassDisplayOnCommand { get; }
    public ICommand BypassDisplayOffCommand { get; }
    public ICommand ClearDisplayHotkeyCommand { get; }

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

    /// <summary>
    /// Routes ramp application through the selected preset's TargetMonitorId:
    /// null (the default, and the only option before multi-monitor support)
    /// keeps the exact original primary-display path with full crash-recovery
    /// coverage; a specific monitor uses the best-effort per-device path
    /// instead (see DisplayManager.TryApplyRampToDevice) and falls back to the
    /// primary path if that device can't be opened (unplugged, driver refusal).
    /// </summary>
    private void ApplyRampToTarget(RAMP ramp)
    {
        var targetId = SelectedDisplayPreset?.TargetMonitorId;
        if (string.IsNullOrEmpty(targetId) || !DisplayManager.Instance.TryApplyRampToDevice(ramp, targetId))
            DisplayManager.Instance.ApplyRamp(ramp);
    }

    private void ApplyDisplayLive()
    {
        var ramp = DisplayManager.Instance.ComputeRamp(
            Gamma, Contrast, ShadowLift, BrightnessOffset, GainRed, GainGreen, GainBlue);

        ApplyRampToTarget(ramp);
        PreviewRampChanged?.Invoke(ramp);
    }

    /// <summary>
    /// Preview-only refresh: re-renders the preview bitmap from current slider
    /// state WITHOUT touching the physical monitor. The screen only changes
    /// on Apply to Screen, preset selection, or a preset hotkey - unless
    /// IsDisplayLive (Freestyle-style) is ON, in which case preview + screen
    /// move together for instant compare.
    /// </summary>
    private void RefreshDisplayPreview()
    {
        var ramp = DisplayManager.Instance.ComputeRamp(
            Gamma, Contrast, ShadowLift, BrightnessOffset, GainRed, GainGreen, GainBlue);
        PreviewRampChanged?.Invoke(ramp);
        if (IsDisplayLive && !IsDisplayBypassed)
            ApplyRampToTarget(ramp);
    }

    /// <summary>Explicit "Apply to Screen": pushes the current preview ramp to the monitor.</summary>
    private void ExecuteApplyDisplayToScreen()
    {
        ApplyDisplayLive();
        StatusMessage = "Applied to screen.";
        if (SelectedDisplayPreset is not null)
            ShowToast($"🖥 {SelectedDisplayPreset.Name}");
    }

    private void ApplyDisplayPresetById(string presetId)
    {
        var preset = DisplayPresets.FirstOrDefault(p => p.Id == presetId);
        if (preset is null)
            return;

        // Hotkey / combo / test-mode activation: select AND push to screen.
        SelectedDisplayPreset = preset;
        ApplyDisplayLive();
    }

    /// <summary>Tray quick-apply: selects AND pushes the preset to screen.</summary>
    public void ApplyDisplayPresetFromTray(DisplayPreset preset)
    {
        SelectedDisplayPreset = preset;
        ApplyDisplayLive();
    }

    /// <summary>Tray quick-apply: selects AND pushes the audio preset.</summary>
    public void ApplyAudioPresetFromTray(AudioPreset preset)
    {
        SelectedAudioPreset = preset;
    }

    /// <summary>True when the combo's pair matches the currently active presets.</summary>
    public bool IsActiveCombo(ComboPreset combo)
        => SelectedDisplayPreset is not null
        && SelectedAudioPreset is not null
        && combo.DisplayPresetId == SelectedDisplayPreset.Id
        && combo.AudioPresetId == SelectedAudioPreset.Id;

    /// <summary>Cycler: move selection one step (wraps) and apply.</summary>
    private void CycleDisplayPreset(int delta)
    {
        if (DisplayPresets.Count == 0)
            return;

        int current = SelectedDisplayPreset is null ? -1 : IndexOfDisplayPreset(SelectedDisplayPreset);
        int next = (current + delta + DisplayPresets.Count) % DisplayPresets.Count;
        SelectedDisplayPreset = DisplayPresets[next];
    }

    private int IndexOfDisplayPreset(DisplayPreset preset)
    {
        for (int i = 0; i < DisplayPresets.Count; i++)
            if (ReferenceEquals(DisplayPresets[i], preset))
                return i;
        return -1;
    }

    /// <summary>
    /// Raised when saving needs a preset name (always for "Save As New", and
    /// for built-in presets which clone into a custom copy). MainWindow
    /// subscribes, shows its InputDialog, and calls back with the result.
    /// </summary>
    public event Func<string, string?, string?, (string Name, string Icon)?>? PresetNameRequested;

    private (string Name, string Icon)? RequestPresetName(string title, string? suggestedName, string? suggestedIcon)
        => PresetNameRequested?.Invoke(title, suggestedName, suggestedIcon);

    private void ExecuteSaveDisplayPreset()
    {
        if (SelectedDisplayPreset is null)
            return;

        // Built-ins are never mutated - saving always forks a named custom copy.
        if (SelectedDisplayPreset.IsBuiltIn)
        {
            // Blank name + user-picked icon: the dialog enforces a non-empty
            // name before its Save button enables (see MainWindow).
            var result = RequestPresetName("Save Display Preset", suggestedName: null, SelectedDisplayPreset.Icon);
            if (result is null)
                return; // cancelled - no half-saved clones

            var custom = SelectedDisplayPreset.Clone();
            custom.Id = Guid.NewGuid().ToString("N");
            custom.Name = result.Value.Name;
            custom.Icon = result.Value.Icon;
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
            SelectedDisplayPreset = custom; // resets dirty state via setter
        }
        else
        {
            // Custom presets keep their name - in-place save of slider state.
            SelectedDisplayPreset.Gamma = Gamma;
            SelectedDisplayPreset.Contrast = Contrast;
            SelectedDisplayPreset.ShadowLift = ShadowLift;
            SelectedDisplayPreset.BrightnessOffset = BrightnessOffset;
            SelectedDisplayPreset.GainRed = GainRed;
            SelectedDisplayPreset.GainGreen = GainGreen;
            SelectedDisplayPreset.GainBlue = GainBlue;
            IsDisplayDirty = false;
        }

        _settings.Save();
        StatusMessage = $"Saved display preset '{SelectedDisplayPreset!.Name}'.";
    }

    #region Preset library (Sonar-style Duplicate / Delete / Favorite)

    private void ExecuteDuplicateDisplayPreset()
    {
        if (SelectedDisplayPreset is null)
            return;
        var src = SelectedDisplayPreset;
        var copy = src.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = TruncatePresetName(src.Name + " Copy");
        copy.IsBuiltIn = false;
        copy.IsFavorite = false;
        copy.HotkeyId = null;
        copy.HotkeyModifiers = 0;
        copy.HotkeyVirtualKey = 0;
        // Carry current slider edits into the copy so Duplicate never loses work.
        copy.Gamma = Gamma;
        copy.Contrast = Contrast;
        copy.ShadowLift = ShadowLift;
        copy.BrightnessOffset = BrightnessOffset;
        copy.GainRed = GainRed;
        copy.GainGreen = GainGreen;
        copy.GainBlue = GainBlue;
        DisplayPresets.Add(copy);
        _settings.DisplayPresets.Add(copy);
        _settings.Save();
        SelectedDisplayPreset = copy;
        StatusMessage = $"Duplicated as '{copy.Name}'.";
        ShowToast($"🖥 {copy.Name}");
    }

    private void ExecuteDeleteDisplayPreset()
    {
        if (SelectedDisplayPreset is null || SelectedDisplayPreset.IsBuiltIn)
        {
            StatusMessage = "Built-in presets can't be deleted - they are the safe defaults.";
            return;
        }
        var doomed = SelectedDisplayPreset;
        if (doomed.HotkeyId.HasValue)
            HotkeyManager.Instance.UnregisterHotkey(doomed.HotkeyId.Value);
        int idx = IndexOfDisplayPreset(doomed);
        DisplayPresets.Remove(doomed);
        _settings.DisplayPresets.RemoveAll(p => p.Id == doomed.Id);
        // Combos pointing at the deleted preset keep their Id but get a readable tombstone.
        RefreshComboNames();
        _settings.Save();
        SelectedDisplayPreset = DisplayPresets.Count > 0
            ? DisplayPresets[Math.Clamp(idx, 0, DisplayPresets.Count - 1)]
            : null;
        if (SelectedDisplayPreset is not null)
            ApplyDisplayPresetById(SelectedDisplayPreset.Id);
        StatusMessage = $"Deleted '{doomed.Name}'.";
    }

    private void ExecuteToggleDisplayFavorite()
    {
        if (SelectedDisplayPreset is null)
            return;
        SelectedDisplayPreset.IsFavorite = !SelectedDisplayPreset.IsFavorite;
        _settings.Save();
        OnPropertyChanged(nameof(FavoriteDisplayPresets));
        StatusMessage = SelectedDisplayPreset.IsFavorite
            ? $"'{SelectedDisplayPreset.Name}' added to favorites. ★"
            : $"'{SelectedDisplayPreset.Name}' removed from favorites.";
    }

    /// <summary>Favorites-first ordering for Home/tray: ★ first, then A-Z.</summary>
    public IEnumerable<DisplayPreset> FavoriteDisplayPresets =>
        DisplayPresets.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name);

    private static string TruncatePresetName(string name)
        => name.Length <= 24 ? name : name[..24];

    private void ExecuteBypassDisplayOn()
    {
        IsDisplayBypassed = true;
        DisplayManager.Instance.RestoreFactoryGamma();
        StatusMessage = "Display bypass: showing factory colors (hold to compare).";
    }

    private void ExecuteBypassDisplayOff()
    {
        IsDisplayBypassed = false;
        ApplyDisplayLive();
        StatusMessage = "Display bypass off.";
    }

    private void ExecuteClearDisplayHotkey()
    {
        if (SelectedDisplayPreset?.HotkeyId.HasValue != true)
            return;
        HotkeyManager.Instance.UnregisterHotkey(SelectedDisplayPreset.HotkeyId.Value);
        SelectedDisplayPreset.HotkeyId = null;
        SelectedDisplayPreset.HotkeyModifiers = 0;
        SelectedDisplayPreset.HotkeyVirtualKey = 0;
        _settings.Save();
        StatusMessage = $"Cleared hotkey for '{SelectedDisplayPreset.Name}'.";
    }

    #endregion

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
            _isAudioDirty = false; // fresh load matches the preset
            OnPropertyChanged(nameof(IsAudioDirty));
            ApplyAudioLive();
            ActiveStateChanged?.Invoke();
        }
    }

    /// <summary>True when the band gains diverge from the selected preset - shows Save.</summary>
    private bool _isAudioDirty;
    public bool IsAudioDirty
    {
        get => _isAudioDirty;
        private set => SetProperty(ref _isAudioDirty, value);
    }

    private void MarkAudioDirty()
    {
        if (!IsAudioDirty)
            IsAudioDirty = true;
    }

    private bool _enableNativeLoudness;
    public bool EnableNativeLoudness
    {
        get => _enableNativeLoudness;
        set { if (SetProperty(ref _enableNativeLoudness, value)) { MarkAudioDirty(); ApplyAudioLive(); } }
    }

    private AudioApplyResult? _lastAudioApplyResult;
    public AudioApplyResult? LastAudioApplyResult
    {
        get => _lastAudioApplyResult;
        private set => SetProperty(ref _lastAudioApplyResult, value);
    }



    public ICommand SelectAudioPresetCommand { get; }
    public ICommand SaveAudioPresetCommand { get; }
    public ICommand RecordAudioHotkeyCommand { get; }
    public ICommand TestAudioPresetCommand { get; }
    public ICommand CycleAudioPresetForwardCommand { get; }
    public ICommand CycleAudioPresetBackCommand { get; }
    public ICommand RecordSelectedAudioHotkeyCommand { get; }
    public ICommand RenameAudioPresetCommand { get; }
    public ICommand DuplicateAudioPresetCommand { get; }
    public ICommand DeleteAudioPresetCommand { get; }
    public ICommand ToggleAudioFavoriteCommand { get; }
    public ICommand ClearAudioHotkeyCommand { get; }

    /// <summary>FxSound-style bypass: flat output without uninstalling the engine.</summary>
    private bool _isAudioBypassed;
    public bool IsAudioBypassed
    {
        get => _isAudioBypassed;
        set { if (SetProperty(ref _isAudioBypassed, value)) ApplyAudioLive(); }
    }

    /// <summary>
    /// Raised whenever the live 10-band gains change so MainWindow can
    /// re-render the response-curve preview. Carries the current gain vector.
    /// </summary>
    public event Action<double[]>? EqGainsChanged;

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

    private void OnBandGainChanged()
    {
        MarkAudioDirty();
        ApplyAudioLive();
    }

    private void ApplyAudioLive()
    {
        var gains = BandGains.Select(b => b.GainDb).ToArray();
        if (gains.Length != AudioManager.BandFrequenciesHz.Length)
            return;

        if (IsAudioBypassed)
        {
            LastAudioApplyResult = AudioManager.Instance.ApplyPreset(new double[10], false);
            EqGainsChanged?.Invoke(gains);
            return;
        }

        LastAudioApplyResult = AudioManager.Instance.ApplyPreset(gains, EnableNativeLoudness);
        EqGainsChanged?.Invoke(gains);
    }

    private void ApplyAudioPresetById(string presetId)
    {
        var preset = AudioPresets.FirstOrDefault(p => p.Id == presetId);
        if (preset is not null)
            SelectedAudioPreset = preset;
    }

    /// <summary>Cycler: move audio selection one step (wraps) and apply.</summary>
    private void CycleAudioPreset(int delta)
    {
        if (AudioPresets.Count == 0)
            return;

        int current = SelectedAudioPreset is null ? -1 : IndexOfAudioPreset(SelectedAudioPreset);
        int next = (current + delta + AudioPresets.Count) % AudioPresets.Count;
        SelectedAudioPreset = AudioPresets[next];
    }

    private int IndexOfAudioPreset(AudioPreset preset)
    {
        for (int i = 0; i < AudioPresets.Count; i++)
            if (ReferenceEquals(AudioPresets[i], preset))
                return i;
        return -1;
    }

    /// <summary>Rename (with emoji picker) for the selected display preset - custom presets only.</summary>
    private void ExecuteRenameDisplayPreset()
    {
        if (SelectedDisplayPreset is null)
            return;
        if (SelectedDisplayPreset.IsBuiltIn)
        {
            StatusMessage = "Built-in presets can't be renamed - Save Preset makes an editable copy.";
            return;
        }

        var result = RequestPresetName("Rename Display Preset", SelectedDisplayPreset.Name, SelectedDisplayPreset.Icon);
        if (result is not null)
        {
            SelectedDisplayPreset.Name = result.Value.Name;
            SelectedDisplayPreset.Icon = result.Value.Icon;
            _settings.Save();
            RefreshComboNames();
            StatusMessage = $"Renamed to '{SelectedDisplayPreset.Name}'.";
        }
    }

    /// <summary>Rename (with emoji picker) for the selected audio preset - custom presets only.</summary>
    private void ExecuteRenameAudioPreset()
    {
        if (SelectedAudioPreset is null)
            return;
        if (SelectedAudioPreset.IsBuiltIn)
        {
            StatusMessage = "Built-in presets can't be renamed - Save Preset makes an editable copy.";
            return;
        }

        var result = RequestPresetName("Rename Audio Preset", SelectedAudioPreset.Name, SelectedAudioPreset.Icon);
        if (result is not null)
        {
            SelectedAudioPreset.Name = result.Value.Name;
            SelectedAudioPreset.Icon = result.Value.Icon;
            _settings.Save();
            RefreshComboNames();
            StatusMessage = $"Renamed to '{SelectedAudioPreset.Name}'.";
        }
    }

    private void ExecuteSaveAudioPreset()
    {
        if (SelectedAudioPreset is null)
            return;

        var gains = BandGains.Select(b => b.GainDb).ToArray();

        if (SelectedAudioPreset.IsBuiltIn)
        {
            var result = RequestPresetName("Save Audio Preset", suggestedName: null, SelectedAudioPreset.Icon);
            if (result is null)
                return; // cancelled

            var custom = SelectedAudioPreset.Clone();
            custom.Id = Guid.NewGuid().ToString("N");
            custom.Name = result.Value.Name;
            custom.Icon = result.Value.Icon;
            custom.IsBuiltIn = false;
            custom.BandGainsDb = gains;
            custom.EnableNativeLoudness = EnableNativeLoudness;
            custom.HotkeyId = null;
            custom.HotkeyModifiers = 0;
            custom.HotkeyVirtualKey = 0;

            AudioPresets.Add(custom);
            _settings.AudioPresets.Add(custom);
            SelectedAudioPreset = custom; // resets dirty state via setter
        }
        else
        {
            SelectedAudioPreset.BandGainsDb = gains;
            SelectedAudioPreset.EnableNativeLoudness = EnableNativeLoudness;
            IsAudioDirty = false;
        }

        _settings.Save();
        StatusMessage = $"Saved audio preset '{SelectedAudioPreset!.Name}'.";
    }

    #region Audio library helpers

    private void ExecuteDuplicateAudioPreset()
    {
        if (SelectedAudioPreset is null)
            return;
        var gains = BandGains.Select(b => b.GainDb).ToArray();
        var copy = SelectedAudioPreset.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = TruncatePresetName(SelectedAudioPreset.Name + " Copy");
        copy.IsBuiltIn = false;
        copy.IsFavorite = false;
        copy.BandGainsDb = gains;
        copy.EnableNativeLoudness = EnableNativeLoudness;
        copy.HotkeyId = null;
        copy.HotkeyModifiers = 0;
        copy.HotkeyVirtualKey = 0;
        AudioPresets.Add(copy);
        _settings.AudioPresets.Add(copy);
        _settings.Save();
        SelectedAudioPreset = copy;
        StatusMessage = $"Duplicated as '{copy.Name}'.";
        ShowToast($"🎧 {copy.Name}");
    }

    private void ExecuteDeleteAudioPreset()
    {
        if (SelectedAudioPreset is null || SelectedAudioPreset.IsBuiltIn)
        {
            StatusMessage = "Built-in presets can't be deleted - they are the safe defaults.";
            return;
        }
        var doomed = SelectedAudioPreset;
        if (doomed.HotkeyId.HasValue)
            HotkeyManager.Instance.UnregisterHotkey(doomed.HotkeyId.Value);
        int idx = IndexOfAudioPreset(doomed);
        AudioPresets.Remove(doomed);
        _settings.AudioPresets.RemoveAll(p => p.Id == doomed.Id);
        RefreshComboNames();
        _settings.Save();
        SelectedAudioPreset = AudioPresets.Count > 0
            ? AudioPresets[Math.Clamp(idx, 0, AudioPresets.Count - 1)]
            : null;
        StatusMessage = $"Deleted '{doomed.Name}'.";
    }

    private void ExecuteToggleAudioFavorite()
    {
        if (SelectedAudioPreset is null)
            return;
        SelectedAudioPreset.IsFavorite = !SelectedAudioPreset.IsFavorite;
        _settings.Save();
        OnPropertyChanged(nameof(FavoriteAudioPresets));
        StatusMessage = SelectedAudioPreset.IsFavorite
            ? $"'{SelectedAudioPreset.Name}' added to favorites. ★"
            : $"'{SelectedAudioPreset.Name}' removed from favorites.";
    }

    public IEnumerable<AudioPreset> FavoriteAudioPresets =>
        AudioPresets.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name);

    private void ExecuteClearAudioHotkey()
    {
        if (SelectedAudioPreset?.HotkeyId.HasValue != true)
            return;
        HotkeyManager.Instance.UnregisterHotkey(SelectedAudioPreset.HotkeyId.Value);
        SelectedAudioPreset.HotkeyId = null;
        SelectedAudioPreset.HotkeyModifiers = 0;
        SelectedAudioPreset.HotkeyVirtualKey = 0;
        _settings.Save();
        StatusMessage = $"Cleared hotkey for '{SelectedAudioPreset.Name}'.";
    }

    #endregion

    #endregion

    #region Built-in EQ engine

    /// <summary>True when the built-in APO is installed and registered.</summary>
    public bool IsEqEngineEnabled => NativeEqEngine.Instance.IsEngineEnabled();

    /// <summary>
    /// True when the render endpoint Windows is CURRENTLY using as the
    /// default output is wired to our APO (falls back to "any device"
    /// if the current default can't be resolved).
    /// </summary>
    public bool IsEqEngineAttached
    {
        get
        {
            var endpointGuid = NativeEqEngine.ExtractEndpointGuid(_lastKnownDefaultRenderDeviceId);
            return endpointGuid is not null
                ? NativeEqEngine.Instance.IsEngineAttachedToEndpoint(endpointGuid)
                : NativeEqEngine.Instance.IsEngineAttachedToAnyDevice();
        }
    }

    /// <summary>
    /// Switched output device (new USB headset, HDMI monitor, etc.) after
    /// enabling? The APO is installed but the device you're using right
    /// now isn't wired to it - sliders move but nothing audible. This is
    /// only re-evaluated when a real device change is detected (see
    /// StartEqDeviceWatch), not on every UI refresh, so it doesn't show up
    /// unless something actually changed. Fix is one more elevated Enable run.
    /// </summary>
    public bool NeedsEqReattach => IsEqEngineEnabled && !IsEqEngineAttached;

    private void RefreshEqEngineUI()
    {
        OnPropertyChanged(nameof(IsEqEngineEnabled));
        OnPropertyChanged(nameof(IsEqEngineAttached));
        OnPropertyChanged(nameof(NeedsEqReattach));
    }

    private DispatcherTimer? _eqDeviceWatchTimer;
    private string? _lastKnownDefaultRenderDeviceId;

    /// <summary>
    /// Polls the current default render device every few seconds and only
    /// re-evaluates the reattach banner when it actually changes - this is
    /// what makes "re-run Enable" a reaction to a real output-device
    /// change instead of a stale, always-on nag.
    /// </summary>
    private void StartEqDeviceWatch()
    {
        _lastKnownDefaultRenderDeviceId = AudioManager.Instance.TryGetDefaultRenderEndpointId();

        _eqDeviceWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _eqDeviceWatchTimer.Tick += (_, _) =>
        {
            var currentId = AudioManager.Instance.TryGetDefaultRenderEndpointId();
            if (!string.Equals(currentId, _lastKnownDefaultRenderDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _lastKnownDefaultRenderDeviceId = currentId;
                RefreshEqEngineUI();
            }
        };
        _eqDeviceWatchTimer.Start();
    }

    private bool _isEqBusy;
    public bool IsEqBusy
    {
        get => _isEqBusy;
        private set => SetProperty(ref _isEqBusy, value);
    }

    private string _eqStatusText = string.Empty;
    public string EqStatusText
    {
        get => _eqStatusText;
        private set => SetProperty(ref _eqStatusText, value);
    }

    public ICommand EnableEqEngineCommand { get; }
    public ICommand DisableEqEngineCommand { get; }

    /// <summary>
    /// One-click enablement: relaunches GamerTool elevated with the
    /// --enable-eq flag; the elevated instance runs the extraction +
    /// registration + endpoint wiring + audio restart, then exits.
    /// </summary>
    private void ExecuteEnableEqEngine()
    {
        var exePath = Environment.ProcessPath!;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--enable-eq",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using var elevated = Process.Start(psi);
            if (elevated is null)
                return;

            IsEqBusy = true;
            EqStatusText = "Enabling built-in EQ - you may hear audio restart...";

            // Wait in the background for the elevated pass to finish.
            Task.Run(async () =>
            {
                try { await elevated.WaitForExitAsync(); } catch { }
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    IsEqBusy = false;
                    bool ok = NativeEqEngine.Instance.IsEngineEnabled();
                    EqStatusText = ok
                        ? "Built-in EQ active on all playback devices."
                        : "Enablement failed - try running Gamer Tool as administrator.";
                    RefreshEqEngineUI();
                    ApplyAudioLive();
                });
            });
        }
        catch
        {
            EqStatusText = "Enablement cancelled (UAC declined).";
        }
    }

    private void ExecuteDisableEqEngine()
    {
        var exePath = Environment.ProcessPath!;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--disable-eq",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using var elevated = Process.Start(psi);
            if (elevated is null)
                return;

            IsEqBusy = true;
            EqStatusText = "Disabling built-in EQ...";

            Task.Run(async () =>
            {
                try { await elevated.WaitForExitAsync(); } catch { }
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    IsEqBusy = false;
                    RefreshEqEngineUI();
                    EqStatusText = NativeEqEngine.Instance.IsEngineEnabled()
                        ? "Disable failed - try running Gamer Tool as administrator."
                        : "Built-in EQ disabled.";
                });
            });
        }
        catch
        {
            EqStatusText = "Disable cancelled (UAC declined).";
        }
    }

    #endregion



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
    public ICommand ToggleComboFavoriteCommand { get; }
    public ICommand ClearComboHotkeyCommand { get; }
    public ICommand DuplicateComboCommand { get; }
    public ICommand ToggleComboAutoLaunchCommand { get; }
    public ICommand RefreshRunningProcessesCommand { get; }

    /// <summary>
    /// Currently running, user-visible apps for the Combos "launch with"
    /// picker. Populated at startup and on-demand via RefreshRunningProcessesCommand
    /// (process lists go stale the moment something new launches).
    /// </summary>
    public ObservableCollection<string> RunningProcesses { get; } = new();

    /// <summary>One selectable row for the Display tab's monitor picker.</summary>
    public sealed record MonitorPickerOption(string? DeviceName, string Label);

    /// <summary>
    /// "Primary Only" (DeviceName == null, the original single-monitor
    /// behavior) plus one entry per additional detected monitor. Loaded
    /// once at startup - monitors rarely change mid-session, and this is a
    /// picker, not a live device-change feed.
    /// </summary>
    public ObservableCollection<MonitorPickerOption> AvailableMonitors { get; } = new();

    private void LoadAvailableMonitors()
    {
        AvailableMonitors.Clear();
        AvailableMonitors.Add(new MonitorPickerOption(null, "Primary Only (recommended)"));
        foreach (var monitor in DisplayManager.EnumerateMonitors())
        {
            if (monitor.IsPrimary)
                continue; // already covered by "Primary Only"
            AvailableMonitors.Add(new MonitorPickerOption(monitor.DeviceName, monitor.FriendlyName));
        }
    }

    private void ExecuteRefreshRunningProcesses()
    {
        RunningProcesses.Clear();
        foreach (var name in GameLaunchWatcher.GetRunningAppProcessNames())
            RunningProcesses.Add(name);
    }

    /// <summary>
    /// Turns auto-launch on/off for a combo, enforcing that each process can
    /// only be claimed by one combo at a time. IsChecked in the view binds
    /// OneWay to AutoActivateOnLaunch, so a rejected toggle here simply never
    /// changes the model and the checkbox visually stays where it was.
    /// </summary>
    private void ExecuteToggleComboAutoLaunch(ComboPreset? combo)
    {
        if (combo is null)
            return;

        if (combo.AutoActivateOnLaunch)
        {
            combo.AutoActivateOnLaunch = false;
            StatusMessage = $"Auto-launch turned off for '{combo.Name}'.";
            _settings.Save();
            return;
        }

        if (string.IsNullOrWhiteSpace(combo.TriggerProcessName))
        {
            StatusMessage = "Pick an app first, then turn on auto-launch.";
            // The CheckBox already flipped its own visual state on click (it's a
            // ToggleButton) even though IsChecked binds OneWay - re-pushing the
            // unchanged value forces PropertyChanged so the binding snaps the
            // visual back to reality instead of showing a checked box that lies.
            combo.RefreshAutoActivateOnLaunch();
            return;
        }

        var conflict = ComboPresets.FirstOrDefault(other =>
            other != combo && other.AutoActivateOnLaunch &&
            string.Equals(other.TriggerProcessName, combo.TriggerProcessName, StringComparison.OrdinalIgnoreCase));

        if (conflict is not null)
        {
            StatusMessage = $"'{conflict.Name}' already auto-launches with {combo.TriggerProcessName} - clear that one first.";
            combo.RefreshAutoActivateOnLaunch();
            return;
        }

        combo.AutoActivateOnLaunch = true;
        StatusMessage = $"'{combo.Name}' will auto-activate whenever {combo.TriggerProcessName} is focused.";
        _settings.Save();
    }

    private DispatcherTimer? _gameLaunchTimer;
    private string? _lastActiveGameLaunchComboId;

    /// <summary>
    /// Polls the foreground window's process every ~1.5s and activates the
    /// combo registered to it, if any - switching seamlessly back and forth
    /// as the user alt-tabs between two registered games. Focus on anything
    /// not registered leaves the last-applied combo alone (does nothing),
    /// which keeps behavior predictable rather than guessing a "revert to".
    /// </summary>
    private void StartGameLaunchWatch()
    {
        ExecuteRefreshRunningProcesses();

        _gameLaunchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _gameLaunchTimer.Tick += (_, _) =>
        {
            var foregroundProcess = GameLaunchWatcher.GetForegroundProcessName();
            if (foregroundProcess is null)
                return;

            var match = ComboPresets.FirstOrDefault(c =>
                c.AutoActivateOnLaunch &&
                string.Equals(c.TriggerProcessName, foregroundProcess, StringComparison.OrdinalIgnoreCase));

            if (match is null || match.Id == _lastActiveGameLaunchComboId)
                return;

            _lastActiveGameLaunchComboId = match.Id;
            ApplyDisplayPresetById(match.DisplayPresetId);
            ApplyAudioPresetById(match.AudioPresetId);
            StatusMessage = $"Auto-activated '{match.Name}' for {foregroundProcess}.";
            ShowToast($"⚡ Auto: {match.Name}");
            ActiveStateChanged?.Invoke();
        };
        _gameLaunchTimer.Start();
    }

    /// <summary>
    /// Live-resolves combo member names by Id (fixes stale denormalized names
    /// after rename/delete). Called after every rename, delete, duplicate, and load.
    /// </summary>
    public void RefreshComboNames()
    {
        var displayById = DisplayPresets.ToDictionary(p => p.Id, p => p.Name);
        var audioById = AudioPresets.ToDictionary(p => p.Id, p => p.Name);
        foreach (var combo in ComboPresets)
        {
            combo.DisplayPresetName = displayById.TryGetValue(combo.DisplayPresetId, out var d)
                ? d : "(deleted preset)";
            combo.AudioPresetName = audioById.TryGetValue(combo.AudioPresetId, out var a)
                ? a : "(deleted preset)";
        }
    }

    public string GetComboSubtitle(ComboPreset combo)
    {
        var displayById = DisplayPresets.ToDictionary(p => p.Id, p => p.Name);
        var audioById = AudioPresets.ToDictionary(p => p.Id, p => p.Name);
        string d = displayById.TryGetValue(combo.DisplayPresetId, out var dn) ? dn : "(deleted)";
        string a = audioById.TryGetValue(combo.AudioPresetId, out var an) ? an : "(deleted)";
        return $"{d}  +  {a}";
    }

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
            Icon = "fire",
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
        ShowToast($"⚡ {combo.Name}");
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
        ShowToast($"⚡ {combo.Name}");
        ActiveStateChanged?.Invoke();
    }

    private void ExecuteToggleComboFavorite(ComboPreset? combo)
    {
        if (combo is null)
            return;
        combo.IsFavorite = !combo.IsFavorite;
        _settings.Save();
        StatusMessage = combo.IsFavorite ? $"'{combo.Name}' favorited. ★" : $"'{combo.Name}' unfavorited.";
    }

    private void ExecuteClearComboHotkey(ComboPreset? combo)
    {
        if (combo?.HotkeyId.HasValue != true)
            return;
        HotkeyManager.Instance.UnregisterHotkey(combo.HotkeyId.Value);
        combo.HotkeyId = null;
        combo.HotkeyModifiers = 0;
        combo.HotkeyVirtualKey = 0;
        _settings.Save();
        StatusMessage = $"Cleared hotkey for '{combo.Name}'.";
    }

    private void ExecuteDuplicateCombo(ComboPreset? combo)
    {
        if (combo is null)
            return;
        var copy = combo.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = TruncatePresetName(combo.Name + " Copy");
        copy.IsFavorite = false;
        copy.HotkeyId = null;
        copy.HotkeyModifiers = 0;
        copy.HotkeyVirtualKey = 0;
        ComboPresets.Add(copy);
        _settings.ComboPresets.Add(copy);
        _settings.Save();
        StatusMessage = $"Duplicated combo as '{copy.Name}'.";
    }

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
        AudioManager.Instance.ClearEq();
        AudioManager.Instance.TrySetNativeLoudnessEqualization(false);
        StatusMessage = "Emergency reset applied: factory display and audio settings restored.";
        ShowToast("↺ Factory reset");
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

    // Pending combination being built - displayed live in the popup; the
    // user confirms with the Set button (ConfirmPendingHotkeyCommand).
    private uint _pendingModifiers;
    private uint _pendingVirtualKey;
    private Key _pendingKey;
    private ModifierKeys _pendingModifierKeys;

    /// <summary>True when the pending combo is complete and Set is enabled.</summary>
    public bool HasPendingHotkey => _pendingVirtualKey != 0;

    /// <summary>
    /// Razer-style live display: shows modifiers as they are held
    /// ("Ctrl + ..."), then the full combo once a main key arrives.
    /// Never stuck on "..." while the user is holding keys.
    /// </summary>
    public string PendingHotkeyLabel
    {
        get
        {
            if (_pendingVirtualKey != 0)
                return HotkeyFormatting.Format(_pendingModifiers, _pendingVirtualKey);
            if (_pendingModifiers != 0)
                return HotkeyFormatting.Format(_pendingModifiers, 0) + " + ...";
            return "Press keys...";
        }
    }

    /// <summary>Keycap pills for the recorder: e.g. ["Ctrl","Alt","D"].</summary>
    public string[] PendingKeycaps
    {
        get
        {
            var caps = new List<string>();
            if ((_pendingModifiers & User32Native.MOD_CONTROL) != 0) caps.Add("Ctrl");
            if ((_pendingModifiers & User32Native.MOD_ALT) != 0) caps.Add("Alt");
            if ((_pendingModifiers & User32Native.MOD_SHIFT) != 0) caps.Add("Shift");
            if ((_pendingModifiers & User32Native.MOD_WIN) != 0) caps.Add("Win");
            if (_pendingVirtualKey != 0)
            {
                var formatted = HotkeyFormatting.Format(0, _pendingVirtualKey);
                caps.Add(string.IsNullOrWhiteSpace(formatted) || formatted == "None" ? "Key" : formatted);
            }
            else if (caps.Count > 0)
            {
                caps.Add("...");
            }
            return caps.ToArray();
        }
    }

    /// <summary>Live conflict hint: empty when OK, red text when taken or missing modifier.</summary>
    public string PendingConflictText
    {
        get
        {
            if (_pendingVirtualKey == 0)
                return _pendingModifiers == 0
                    ? "Hold Ctrl / Alt / Shift / Win, then add a key."
                    : "Now press a main key (A-Z, 0-9, F1-F12).";
            if (_pendingModifiers == 0)
                return "Add at least one modifier - bare keys never fire in-game.";
            try
            {
                // Probe without claiming: if our own current binding owns it, it's fine.
                int? existingId = _recordingTarget != null
                    ? GetExistingHotkeyId(_recordingTarget, _recordingKind)
                    : null;
                if (existingId.HasValue)
                    HotkeyManager.Instance.UnregisterHotkey(existingId.Value);
                bool free = HotkeyManager.Instance.IsCombinationAvailable(_pendingModifiers, _pendingVirtualKey);
                if (existingId.HasValue)
                    ReclaimPreviousHotkey(_recordingTarget!, _recordingKind, existingId.Value);
                return free ? "Ready - press Set. ✓" : "Already in use - try another combo.";
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public bool IsPendingConflict
    {
        get
        {
            if (_pendingVirtualKey == 0 || _pendingModifiers == 0)
                return true;
            try { return !HotkeyManager.Instance.IsCombinationAvailable(_pendingModifiers, _pendingVirtualKey); }
            catch { return false; }
        }
    }

    public ICommand ConfirmPendingHotkeyCommand { get; }
    public ICommand ClearPendingHotkeyCommand { get; }

    public void BeginHotkeyRecording(object target, HotkeyTargetKind kind, string label)
    {
        _recordingTarget = target;
        _recordingKind = kind;
        RecordingTargetLabel = label;
        _pendingModifiers = 0;
        _pendingVirtualKey = 0;
        IsRecordingHotkey = true;
        RefreshPendingHotkeyUI();
        StatusMessage = $"Press a new key combination for '{label}'...";
    }

    private void RefreshPendingHotkeyUI()
    {
        OnPropertyChanged(nameof(HasPendingHotkey));
        OnPropertyChanged(nameof(PendingHotkeyLabel));
        OnPropertyChanged(nameof(PendingKeycaps));
        OnPropertyChanged(nameof(PendingConflictText));
        OnPropertyChanged(nameof(IsPendingConflict));
        (ConfirmPendingHotkeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void CancelHotkeyRecording()
    {
        IsRecordingHotkey = false;
        _recordingTarget = null;
        StatusMessage = "Hotkey recording cancelled.";
    }

    /// <summary>
    /// Called by MainWindow.xaml.cs from PreviewKeyDown/PreviewKeyUp while
    /// IsRecordingHotkey is true. Builds the pending combination live -
    /// modifiers alone update the pills immediately (Razer-style), nothing is
    /// bound until the user clicks Set.
    /// </summary>
    public void CaptureHotkeyInput(ModifierKeys modifiers, Key key)
    {
        if (!IsRecordingHotkey)
            return;

        uint win32Modifiers = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) win32Modifiers |= User32Native.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) win32Modifiers |= User32Native.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) win32Modifiers |= User32Native.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) win32Modifiers |= User32Native.MOD_WIN;

        bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

        if (isModifierOnly)
        {
            // Live-update pills while holding modifiers, before any main key.
            _pendingModifiers = win32Modifiers;
            _pendingModifierKeys = modifiers;
            RefreshPendingHotkeyUI();
            return;
        }

        // A plain non-modifier key without modifiers is shown too (so the
        // popup reacts) but Set stays disabled until a modifier is included.
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _pendingModifiers = win32Modifiers;
        _pendingVirtualKey = virtualKey;
        _pendingKey = key;
        _pendingModifierKeys = modifiers;

        RefreshPendingHotkeyUI();
    }

    /// <summary>Called on KeyUp so releasing back to modifiers-only still shows live pills.</summary>
    public void CaptureModifierRelease(ModifierKeys modifiers)
    {
        if (!IsRecordingHotkey || _pendingVirtualKey != 0)
            return;
        uint win32Modifiers = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) win32Modifiers |= User32Native.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) win32Modifiers |= User32Native.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) win32Modifiers |= User32Native.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) win32Modifiers |= User32Native.MOD_WIN;
        _pendingModifiers = win32Modifiers;
        _pendingModifierKeys = modifiers;
        RefreshPendingHotkeyUI();
    }

    /// <summary>Commits the pending combination: registers it and binds the target.</summary>
    private void ExecuteConfirmPendingHotkey()
    {
        if (!IsRecordingHotkey || _recordingTarget is null || _pendingVirtualKey == 0)
            return;

        if (_pendingModifiers == 0)
        {
            StatusMessage = "Hotkeys must include at least one modifier (Ctrl, Alt, Shift, or Win).";
            return;
        }

        // If the target already owns a hotkey, release its current claim first
        // so re-binding the exact combination it already holds is a success.
        int? existingId = GetExistingHotkeyId(_recordingTarget, _recordingKind);
        if (existingId.HasValue)
            HotkeyManager.Instance.UnregisterHotkey(existingId.Value);

        if (!HotkeyManager.Instance.IsCombinationAvailable(_pendingModifiers, _pendingVirtualKey))
        {
            if (existingId.HasValue)
                ReclaimPreviousHotkey(_recordingTarget, _recordingKind, existingId.Value);

            StatusMessage = "That combination is already in use by another application. Try a different one.";
            return;
        }

        Action action = BuildHotkeyAction(_recordingTarget, _recordingKind);
        int newId = HotkeyManager.Instance.RegisterHotkey(_pendingModifiers, _pendingVirtualKey, action, RecordingTargetLabel);

        ApplyHotkeyBindingToTarget(_recordingTarget, _recordingKind, newId, _pendingModifiers, _pendingVirtualKey);

        _settings.Save();
        IsRecordingHotkey = false;
        _recordingTarget = null;
        StatusMessage = $"Bound '{RecordingTargetLabel}' to {HotkeyFormatting.Format(_pendingModifiers, _pendingVirtualKey)}.";
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
        HotkeyTargetKind.Display => () =>
        {
            var p = (DisplayPreset)target;
            ApplyDisplayPresetById(p.Id);
            ShowToast($"🖥 {p.Name}");
            ActiveStateChanged?.Invoke();
        },
        HotkeyTargetKind.Audio => () =>
        {
            var p = (AudioPreset)target;
            ApplyAudioPresetById(p.Id);
            ShowToast($"🎧 {p.Name}");
            ActiveStateChanged?.Invoke();
        },
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
        private set { if (SetProperty(ref _testModeSecondsRemaining, value)) OnPropertyChanged(nameof(TestModeText)); }
    }

    /// <summary>Compact one-line text for the test-mode banner overlay.</summary>
    public string TestModeText => $"TESTING - REVERTING IN {TestModeSecondsRemaining}s";

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

    #region Status + OSD toast (Freestyle-style confirm)

    private string _statusMessage = "Ready.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private string _toastText = string.Empty;
    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    private bool _isToastVisible;
    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// Freestyle-style OSD: big pill that confirms a hotkey/tray activation
    /// without stealing focus. Auto-hides after 1.6s.
    /// </summary>
    public void ShowToast(string text)
    {
        ToastText = text;
        IsToastVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            _toastTimer = null;
            IsToastVisible = false;
        };
        _toastTimer.Start();
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
        CycleDisplayPresetForwardCommand = new RelayCommand(() => CycleDisplayPreset(1));
        CycleDisplayPresetBackCommand = new RelayCommand(() => CycleDisplayPreset(-1));
        ApplyDisplayToScreenCommand = new RelayCommand(ExecuteApplyDisplayToScreen);
        RecordSelectedDisplayHotkeyCommand = new RelayCommand(() =>
        {
            if (SelectedDisplayPreset is not null) BeginHotkeyRecording(SelectedDisplayPreset, HotkeyTargetKind.Display, SelectedDisplayPreset.Name);
        });
        RenameDisplayPresetCommand = new RelayCommand(ExecuteRenameDisplayPreset);
        DuplicateDisplayPresetCommand = new RelayCommand(ExecuteDuplicateDisplayPreset);
        DeleteDisplayPresetCommand = new RelayCommand(ExecuteDeleteDisplayPreset);
        ToggleDisplayFavoriteCommand = new RelayCommand(ExecuteToggleDisplayFavorite);
        BypassDisplayOnCommand = new RelayCommand(ExecuteBypassDisplayOn);
        BypassDisplayOffCommand = new RelayCommand(ExecuteBypassDisplayOff);
        ClearDisplayHotkeyCommand = new RelayCommand(ExecuteClearDisplayHotkey);

        SelectAudioPresetCommand = new RelayCommand<AudioPreset>(p => { if (p is not null) SelectedAudioPreset = p; });
        SaveAudioPresetCommand = new RelayCommand(ExecuteSaveAudioPreset);
        RecordAudioHotkeyCommand = new RelayCommand<AudioPreset>(p => { if (p is not null) BeginHotkeyRecording(p, HotkeyTargetKind.Audio, p.Name); });
        TestAudioPresetCommand = new RelayCommand<AudioPreset>(ExecuteTestAudioPreset);
        CycleAudioPresetForwardCommand = new RelayCommand(() => CycleAudioPreset(1));
        CycleAudioPresetBackCommand = new RelayCommand(() => CycleAudioPreset(-1));
        RecordSelectedAudioHotkeyCommand = new RelayCommand(() =>
        {
            if (SelectedAudioPreset is not null) BeginHotkeyRecording(SelectedAudioPreset, HotkeyTargetKind.Audio, SelectedAudioPreset.Name);
        });
        RenameAudioPresetCommand = new RelayCommand(ExecuteRenameAudioPreset);
        DuplicateAudioPresetCommand = new RelayCommand(ExecuteDuplicateAudioPreset);
        DeleteAudioPresetCommand = new RelayCommand(ExecuteDeleteAudioPreset);
        ToggleAudioFavoriteCommand = new RelayCommand(ExecuteToggleAudioFavorite);
        ClearAudioHotkeyCommand = new RelayCommand(ExecuteClearAudioHotkey);

        CreateComboCommand = new RelayCommand(ExecuteCreateCombo);
        DeleteComboCommand = new RelayCommand<ComboPreset>(ExecuteDeleteCombo);
        RecordComboHotkeyCommand = new RelayCommand<ComboPreset>(c => { if (c is not null) BeginHotkeyRecording(c, HotkeyTargetKind.Combo, c.Name); });
        ActivateComboCommand = new RelayCommand<ComboPreset>(c => { if (c is not null) ActivateCombo(c); });
        ToggleComboFavoriteCommand = new RelayCommand<ComboPreset>(ExecuteToggleComboFavorite);
        ClearComboHotkeyCommand = new RelayCommand<ComboPreset>(ExecuteClearComboHotkey);
        DuplicateComboCommand = new RelayCommand<ComboPreset>(ExecuteDuplicateCombo);
        ToggleComboAutoLaunchCommand = new RelayCommand<ComboPreset>(ExecuteToggleComboAutoLaunch);
        RefreshRunningProcessesCommand = new RelayCommand(ExecuteRefreshRunningProcesses);

        PanicResetCommand = new RelayCommand(ExecutePanicReset);
        ConfirmPendingHotkeyCommand = new RelayCommand(ExecuteConfirmPendingHotkey, () => HasPendingHotkey && _pendingModifiers != 0);
        ClearPendingHotkeyCommand = new RelayCommand(() =>
        {
            _pendingModifiers = 0;
            _pendingVirtualKey = 0;
            RefreshPendingHotkeyUI();
        });

        EnableEqEngineCommand = new RelayCommand(ExecuteEnableEqEngine);
        DisableEqEngineCommand = new RelayCommand(ExecuteDisableEqEngine);

        // Fire-and-forget: detection + version checks hit the network; the
        // constructor must not block first paint on them.

        foreach (var preset in _settings.DisplayPresets)
            DisplayPresets.Add(preset);
        foreach (var preset in _settings.AudioPresets)
            AudioPresets.Add(preset);
        foreach (var combo in _settings.ComboPresets)
            ComboPresets.Add(combo);
        RefreshComboNames();

        // Registry is ground truth for "Run on Startup" - a stale
        // settings.json (e.g. the user removed the Run key by hand) should
        // never leave the toggle showing a state that isn't real.
        _runOnStartup = StartupManager.IsEnabled();
        _settings.RunOnStartup = _runOnStartup;

        // validates it against what's actually installed and auto-picks when None.
        // leaves an elevated instance running that ignores our commands.

        var initialDisplay = DisplayPresets.FirstOrDefault(p => p.Id == _settings.ActiveDisplayPresetId)
                              ?? DisplayPresets.FirstOrDefault();
        var initialAudio = AudioPresets.FirstOrDefault(p => p.Id == _settings.ActiveAudioPresetId)
                            ?? AudioPresets.FirstOrDefault();

        if (initialDisplay is not null)
            SelectedDisplayPreset = initialDisplay;
        if (initialAudio is not null)
            SelectedAudioPreset = initialAudio;

        LoadAvailableMonitors();
        StartEqDeviceWatch();
        StartGameLaunchWatch();
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
