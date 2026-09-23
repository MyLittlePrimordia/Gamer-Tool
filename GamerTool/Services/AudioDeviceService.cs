using NAudio.CoreAudioApi;

namespace GamerTool.Services;

public record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault);

/// <summary>
/// Enumerates active playback (render) endpoints via NAudio's MMDevice API.
/// Hand-rolled COM interop was crashing the process (AccessViolation) when
/// the device dropdown opened — NAudio's wrappers are the reliable path.
/// </summary>
public static class AudioDeviceService
{
    public static List<AudioDeviceInfo> EnumerateRenderDevices()
    {
        var results = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                defaultId = def?.ID;
            }
            catch
            {
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def?.ID;
                }
                catch { /* no default device */ }
            }

            // MMDeviceCollection is NOT IDisposable in NAudio 2.2 — do not wrap in using.
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < collection.Count; i++)
            {
                try
                {
                    var device = collection[i];
                    if (device == null) continue;

                    string id = device.ID ?? "";
                    string name = device.FriendlyName ?? "Unknown Device";
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    results.Add(new AudioDeviceInfo(id, name, id == defaultId));
                }
                catch
                {
                    // Skip a single bad endpoint; keep enumerating the rest.
                }
            }
        }
        catch
        {
            // Return whatever we collected (possibly empty). Never throw to UI.
        }

        return results;
    }
}
