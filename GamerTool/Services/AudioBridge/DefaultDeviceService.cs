using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace GamerTool.Services.AudioBridge;

/// <summary>
/// Wraps AudioSwitcher.AudioApi.CoreAudio for the one thing the Bridge needs:
/// programmatically setting the system default PLAYBACK device (both the
/// "Console" and "Multimedia" roles — the same two roles Windows' own Sound
/// Settings "Output device" picker sets together). We deliberately don't
/// hand-write the underlying IPolicyConfig COM interface ourselves: it's
/// undocumented, its vtable layout differs across Windows versions, and a
/// wrong guess there risks a hard crash instead of a clean failure — not a
/// risk worth taking blind. AudioSwitcher already does this correctly and is
/// widely relied on for exactly this.
/// </summary>
public static class DefaultDeviceService
{
    private static CoreAudioController? _controller;
    private static CoreAudioController Controller => _controller ??= new CoreAudioController();

    public static Guid? GetDefaultPlaybackDeviceId() => Controller.DefaultPlaybackDevice?.Id;

    public static string? GetDefaultPlaybackDeviceName() => Controller.DefaultPlaybackDevice?.FullName;

    /// <summary>Sets both default roles to the given device. Returns false
    /// (without throwing) if the device can't be found or the switch fails —
    /// callers should treat that as "didn't switch" and fall back safely
    /// rather than assume audio is now routed where they think it is.</summary>
    public static bool SetDefaultPlaybackDevice(Guid deviceId)
    {
        try
        {
            var device = Controller.GetDevice(deviceId);
            if (device == null) return false;

            bool okDefault = device.SetAsDefault();
            bool okComms = device.SetAsDefaultCommunications();
            return okDefault && okComms;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Finds an active playback device whose name contains the given
    /// text (case-insensitive) — used to locate the virtual cable's "Input"
    /// endpoint by its known friendly name.</summary>
    public static Guid? FindPlaybackDeviceIdByNameContains(string nameFragment)
    {
        try
        {
            foreach (var device in Controller.GetPlaybackDevices())
            {
                if (device.State == DeviceState.Active &&
                    device.FullName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
                {
                    return device.Id;
                }
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    public static string? GetDeviceNameById(Guid deviceId)
    {
        try { return Controller.GetDevice(deviceId)?.FullName; }
        catch { return null; }
    }
}
