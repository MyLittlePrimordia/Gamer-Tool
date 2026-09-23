namespace GamerTool.Models;

/// <summary>
/// The full persisted state of the app: user presets, hotkey bindings, and
/// the last-applied display/audio settings so GamerTool can restore them on
/// launch (e.g. after a reboot) without the user having to re-pick a preset.
/// </summary>
public class AppSettings
{
    public List<DisplayPreset> DisplayPresets { get; set; } = DisplayPreset.Defaults;
    public List<AudioPreset> AudioPresets { get; set; } = AudioPreset.Defaults;
    public List<ComboPreset> ComboPresets { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    public string LastDisplayPreset { get; set; } = "Daylight / Competitive";
    public string LastAudioPreset { get; set; } = "Flat";

    /// <summary>Selected WASAPI render endpoint ID, empty = system default device.</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>Set true once the one-time elevated audio-engine setup has succeeded.</summary>
    public bool AudioEngineInitialized { get; set; } = false;

    public bool StartMinimizedToTray { get; set; } = true;
    public bool LaunchOnWindowsStartup { get; set; } = false;
    public bool ShowOsdToast { get; set; } = true;
}
