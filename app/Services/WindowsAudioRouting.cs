using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using GamerTool.Models;
using NAudio.CoreAudioApi;

namespace GamerTool.Services;

/// <summary>One of the four Windows default audio roles a slot can move.</summary>
public enum WindowsAudioFlow
{
    Playback,
    PlaybackCommunications,
    Recording,
    RecordingCommunications,
}

public static class WindowsAudioFlowExtensions
{
    public static bool IsRecording(this WindowsAudioFlow flow) =>
        flow is WindowsAudioFlow.Recording or WindowsAudioFlow.RecordingCommunications;

    public static bool IsCommunications(this WindowsAudioFlow flow) =>
        flow is WindowsAudioFlow.PlaybackCommunications or WindowsAudioFlow.RecordingCommunications;
}

/// <summary>
/// What Windows had before a slot moved it, and what the slot moved it to.
/// <para>
/// First before and last after, per flow. A second slot may move the same
/// endpoint again; restoring the second slot's starting point would put the
/// first slot's headset back, which is not where the user was before any of
/// this. And the after value is not bookkeeping: a restore only runs while the
/// current default still equals it, so a user who changed the device by hand
/// during the game keeps that choice.
/// </para>
/// </summary>
public sealed class WindowsAudioRouteMemory
{
    private readonly Dictionary<WindowsAudioFlow, WindowsAudioDefault> _entries = new();

    public int Count => _entries.Count;

    public IReadOnlyList<WindowsAudioFlow> Flows => _entries.Keys.ToList();

    public void Remember(WindowsAudioFlow flow, string before, string after)
    {
        if (string.IsNullOrWhiteSpace(before)
            || string.IsNullOrWhiteSpace(after)
            || string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_entries.TryGetValue(flow, out WindowsAudioDefault? remembered))
        {
            _entries[flow] = remembered with { After = after };
        }
        else
        {
            _entries[flow] = new WindowsAudioDefault(before, after);
        }
    }

    /// <summary>
    /// Whether this flow still belongs to the app, and what to put back if so.
    /// Does not forget the entry; the caller does that after trying the switch,
    /// including when the switch fails, because a second reset should not keep
    /// retrying a device that is gone.
    /// </summary>
    public bool ShouldRestore(WindowsAudioFlow flow, string current, out string before)
    {
        before = string.Empty;

        if (!_entries.TryGetValue(flow, out WindowsAudioDefault? remembered)
            || !string.Equals(current, remembered.After, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        before = remembered.Before;
        return true;
    }

    public void Forget(WindowsAudioFlow flow) => _entries.Remove(flow);

    private sealed record WindowsAudioDefault(string Before, string After);
}

/// <summary>One endpoint switch a slot has asked for.</summary>
public sealed record WindowsAudioRoute(WindowsAudioFlow Flow, string DeviceId, string DeviceName)
{
    public string Label => Flow switch
    {
        WindowsAudioFlow.Playback => "playback",
        WindowsAudioFlow.PlaybackCommunications => "voice chat output",
        WindowsAudioFlow.Recording => "microphone",
        _ => "voice chat microphone",
    };
}

/// <summary>
/// What a slot's Windows audio route would do, worked out from values that have
/// already been read.
/// <para>
/// The interesting decision here is the FxSound one. FxSound only processes what
/// Windows sends to it, so pointing the default playback at a physical headset
/// while FxSound is running turns its equalizer off for everything that follows
/// the default. That is allowed, but only when the slot says so; otherwise the
/// playback half is left exactly where it was and the plan says why. The
/// microphone half is independent and is still applied.
/// </para>
/// <para>
/// This is kept separate from the COM call for the same reason
/// <see cref="OutputRouting"/> is separate from the device enumeration: choosing
/// the wrong endpoint is immediately audible, and the rule can be tested without
/// a sound card.
/// </para>
/// </summary>
public sealed class WindowsAudioRoutePlan
{
    private WindowsAudioRoutePlan(
        IReadOnlyList<WindowsAudioRoute> routes,
        IReadOnlyList<string> notes,
        string summary)
    {
        Routes = routes;
        Notes = notes;
        Summary = summary;
    }

    public IReadOnlyList<WindowsAudioRoute> Routes { get; }

    /// <summary>Reasons a requested route was not planned, one short sentence each.</summary>
    public IReadOnlyList<string> Notes { get; }

    public string Summary { get; }

    public bool HasRoutes => Routes.Count > 0;

    public static WindowsAudioRoutePlan Empty { get; } =
        new(Array.Empty<WindowsAudioRoute>(), Array.Empty<string>(), string.Empty);

    public static WindowsAudioRoutePlan Build(
        HotkeySlot slot,
        IReadOnlyList<AudioDeviceInfo> playbackDevices,
        IReadOnlyList<AudioDeviceInfo> recordingDevices,
        bool fxSoundRunning)
    {
        List<WindowsAudioRoute> routes = new();
        List<string> notes = new();

        AudioDeviceInfo? playback = Find(playbackDevices, slot.WindowsPlaybackDeviceId);
        if (!string.IsNullOrWhiteSpace(slot.WindowsPlaybackDeviceId) && playback is null)
        {
            notes.Add("Playback device is not connected");
        }

        if (playback is not null)
        {
            bool bypassesFxSound = !IsFxSoundEndpoint(playback.Name);

            if (fxSoundRunning && bypassesFxSound && !slot.WindowsAudioBypassFxSound)
            {
                notes.Add("FxSound is on, playback left alone");
            }
            else
            {
                routes.Add(new WindowsAudioRoute(
                    WindowsAudioFlow.Playback,
                    playback.Id,
                    playback.Name));

                if (slot.WindowsPlaybackCommunications)
                {
                    routes.Add(new WindowsAudioRoute(
                        WindowsAudioFlow.PlaybackCommunications,
                        playback.Id,
                        playback.Name));
                }
            }
        }

        AudioDeviceInfo? recording = Find(recordingDevices, slot.WindowsRecordingDeviceId);
        if (!string.IsNullOrWhiteSpace(slot.WindowsRecordingDeviceId) && recording is null)
        {
            notes.Add("Microphone is not connected");
        }

        if (recording is not null)
        {
            routes.Add(new WindowsAudioRoute(
                WindowsAudioFlow.Recording,
                recording.Id,
                recording.Name));

            if (slot.WindowsRecordingCommunications)
            {
                routes.Add(new WindowsAudioRoute(
                    WindowsAudioFlow.RecordingCommunications,
                    recording.Id,
                    recording.Name));
            }
        }

        return new WindowsAudioRoutePlan(routes, notes, Describe(routes));
    }

    private static AudioDeviceInfo? Find(IReadOnlyList<AudioDeviceInfo> devices, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Id only. A friendly name is not unique, and matching one would let a
        // replacement device inherit a route the user chose for a different
        // piece of hardware. A vanished id is a warning the user can act on;
        // silently landing on the wrong speakers is not.
        foreach (AudioDeviceInfo device in devices)
        {
            if (string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// FxSound's own endpoint is part of the chain, not a bypass. Matching the
    /// name is the only signal available without reading the driver's private
    /// properties; if FxSound ever renames it, the unbypassed plan will skip
    /// playback and say so, which is the safe direction.
    /// </summary>
    private static bool IsFxSoundEndpoint(string name) =>
        name.Contains("FxSound", StringComparison.OrdinalIgnoreCase);

    private static string Describe(IReadOnlyList<WindowsAudioRoute> routes)
    {
        bool playback = routes.Any(route =>
            route.Flow is WindowsAudioFlow.Playback or WindowsAudioFlow.PlaybackCommunications);
        bool microphone = routes.Any(route =>
            route.Flow is WindowsAudioFlow.Recording or WindowsAudioFlow.RecordingCommunications);
        bool chat = routes.Any(route =>
            route.Flow is WindowsAudioFlow.PlaybackCommunications or WindowsAudioFlow.RecordingCommunications);

        List<string> parts = new();
        if (playback)
        {
            parts.Add("playback");
        }

        if (microphone)
        {
            parts.Add("microphone");
        }

        if (chat)
        {
            parts.Add("voice chat");
        }

        return string.Join(" + ", parts);
    }
}

public sealed class WindowsAudioApplyResult
{
    public WindowsAudioApplyResult(
        IReadOnlyList<WindowsAudioRoute> applied,
        IReadOnlyList<string> warnings,
        string summary)
    {
        Applied = applied;
        Warnings = warnings;
        Summary = summary;
    }

    public IReadOnlyList<WindowsAudioRoute> Applied { get; }

    public IReadOnlyList<string> Warnings { get; }

    public string Summary { get; }

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// Applies a <see cref="WindowsAudioRoutePlan"/> to Windows and proves each
/// switch by reading the default back.
/// <para>
/// There is no documented API for changing the default endpoint. The policy
/// client used here is the interface Windows Settings itself goes through, and it
/// is reached through its registered class rather than through its vtable. It
/// can return success without changing anything, which is why every setter is
/// followed by a read rather than trusted.
/// </para>
/// <para>
/// One COM object is created per apply. The call is quick and the object is not
/// kept: keeping one alive across device changes is how a stale endpoint proxy
/// becomes the thing the next switch talks to.
/// </para>
/// </summary>
public static class WindowsAudioRouting
{
    public static WindowsAudioRoutePlan Plan(
        HotkeySlot slot,
        IReadOnlyList<AudioDeviceInfo> playbackDevices,
        IReadOnlyList<AudioDeviceInfo> recordingDevices,
        bool fxSoundRunning) =>
        WindowsAudioRoutePlan.Build(slot, playbackDevices, recordingDevices, fxSoundRunning);

    public static WindowsAudioApplyResult Apply(
        WindowsAudioRoutePlan plan,
        AudioDeviceManager devices)
    {
        List<WindowsAudioRoute> applied = new();
        List<string> warnings = new(plan.Notes);

        if (!plan.HasRoutes)
        {
            return new WindowsAudioApplyResult(applied, warnings, plan.Summary);
        }

        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            IPolicyConfig policy = (IPolicyConfig)client;

            foreach (WindowsAudioRoute route in plan.Routes)
            {
                int hr = policy.SetDefaultEndpoint(route.DeviceId, RoleFor(route.Flow));
                if (hr < 0)
                {
                    warnings.Add("Windows refused " + route.Label + " to " + route.DeviceName);
                    TraceLog.Write("ROUTE " + route.Label + " refused hresult=0x" + hr.ToString("X8"));
                    continue;
                }

                if (!Verify(route, devices))
                {
                    // The legacy setter is documented by everyone who has touched
                    // it to sometimes return S_OK and do nothing. A silent success
                    // is worse than a refusal, so it is reported as one.
                    warnings.Add("Windows did not keep " + route.Label + " on " + route.DeviceName);
                    TraceLog.Write("ROUTE " + route.Label + " not verified on " + route.DeviceName);
                    continue;
                }

                applied.Add(route);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("ROUTE apply", ex);
            warnings.Add("Windows would not change the default audio device");
        }
        finally
        {
            Release(client);
        }

        return new WindowsAudioApplyResult(applied, warnings, plan.Summary);
    }

    /// <summary>
    /// Puts one flow back on one endpoint. Used by the restore path, where the
    /// caller has already decided that the user has not changed it by hand.
    /// </summary>
    public static bool TrySetDefault(
        WindowsAudioFlow flow,
        string deviceId,
        AudioDeviceManager devices,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "No endpoint to restore";
            return false;
        }

        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            IPolicyConfig policy = (IPolicyConfig)client;

            int hr = policy.SetDefaultEndpoint(deviceId, RoleFor(flow));
            if (hr < 0)
            {
                error = "hresult=0x" + hr.ToString("X8");
                return false;
            }

            if (!Verify(flow, deviceId, devices))
            {
                error = "the switch was not kept";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Release(client);
        }
    }

    private static bool Verify(WindowsAudioRoute route, AudioDeviceManager devices) =>
        Verify(route.Flow, route.DeviceId, devices);

    private static bool Verify(
        WindowsAudioFlow flow,
        string deviceId,
        AudioDeviceManager devices)
    {
        // The setter is synchronous in the usual case, but endpoint switches
        // cross the audio service and a device that has just appeared can answer
        // a fraction late. One short retry avoids reporting a switch that did
        // happen as a failure; more than one would make a four-route slot sit on
        // the dispatcher for longer than the user should wait for a menu click.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string current = devices.GetDefaultDeviceId(flow.IsRecording(), flow.IsCommunications());
            if (string.Equals(current, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (attempt == 0)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private static Role RoleFor(WindowsAudioFlow flow) =>
        flow.IsCommunications() ? Role.Communications : Role.Multimedia;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // The RCW is going away with the call either way.
            }
        }
    }

    /// <summary>
    /// IPolicyConfig's vtable. Every method before <see cref="SetDefaultEndpoint"/>
    /// is declared even though this app never calls it: the CLR maps interface
    /// methods by position, so deleting one would make the setter call a
    /// different entry point. The pointer-shaped arguments are deliberately
    /// opaque; none of the preceding methods is invoked.
    /// </summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool isDefault,
            out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);

        [PreserveSig]
        int SetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr endpointFormat,
            IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool isDefault,
            out long defaultPeriod,
            out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            ref long period);

        [PreserveSig]
        int GetShareMode(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            out IntPtr mode);

        [PreserveSig]
        int SetShareMode(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr key,
            out IntPtr value);

        [PreserveSig]
        int SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr key,
            IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            Role role);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClient
    {
    }
}
