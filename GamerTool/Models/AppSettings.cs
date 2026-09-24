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

    /// <summary>The real physical playback device (name) that was the Windows
    /// default before the Bridge switched it to the virtual cable. Used by
    /// the startup self-heal check to restore it if GamerTool didn't exit
    /// cleanly last time — see MainWindow_Loaded / AudioBridge README notes.</summary>
    public string BridgeRealDeviceName { get; set; } = "";

    /// <summary>True only while the Bridge is deliberately routing default
    /// audio through the virtual cable. Cleared on every clean shutdown; if
    /// this is still true on the NEXT launch, it means GamerTool didn't exit
    /// cleanly last time and the self-heal check should restore audio.</summary>
    public bool BridgeActive { get; set; } = false;

    public bool StartMinimizedToTray { get; set; } = true;
    public bool LaunchOnWindowsStartup { get; set; } = false;
    public bool ShowOsdToast { get; set; } = true;
}
