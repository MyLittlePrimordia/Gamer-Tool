using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GamerTool.Services;

/// <summary>
/// The real version of the setup check: the calls that touch a screen, the
/// compositor and the audio endpoints.
/// <para>
/// Split from <see cref="SetupCheckRunner"/> on purpose. The runner decides what
/// gets checked and how the answer is reported, and it can be tested anywhere;
/// everything in here needs this machine's hardware and can only be judged by
/// running it.
/// </para>
/// </summary>
public sealed class WindowsSetupProbe
{
    /// <summary>
    /// How long one HDR flip may take before the check calls it a failure. Long
    /// enough for a monitor that renegotiates the link, short enough that a check
    /// the user is watching does not feel broken.
    /// </summary>
    private static readonly TimeSpan HdrTimeout = TimeSpan.FromSeconds(4);

    private readonly DisplayService _display;
    private readonly HdrControl _hdr;
    private readonly AudioDeviceManager _devices;
    private readonly Func<bool>? _fxSoundRunning;

    public WindowsSetupProbe(
        DisplayService display,
        HdrControl hdr,
        AudioDeviceManager devices,
        Func<bool>? fxSoundRunning = null)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _hdr = hdr ?? throw new ArgumentNullException(nameof(hdr));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _fxSoundRunning = fxSoundRunning;
    }

    public IReadOnlyList<SetupScreen> Screens()
    {
        List<SetupScreen> screens = new();

        foreach (MonitorChoice monitor in _display.MonitorChoices)
        {
            HdrReading reading = HdrReading.Unknown;

            try
            {
                reading = _hdr.Read(monitor.Device);
            }
            catch (Exception ex)
            {
                // A screen that will not answer for HDR is still worth testing for
                // gamma, so it goes in the list without HDR rather than being
                // dropped and leaving the user with "no screen was found".
                TraceLog.Write("SETUP hdr read", ex);
            }

            screens.Add(new SetupScreen(
                monitor.Device,
                monitor.Key,
                string.IsNullOrWhiteSpace(monitor.Name) ? monitor.Device : monitor.Name,
                reading.Known && reading.Supported,
                reading.Known && reading.Active));
        }

        return screens;
    }

    public SetupCheckItem Gamma(string device)
    {
        GammaProbe probe = _display.ProbeGamma(device);

        if (!probe.Ok)
        {
            return new SetupCheckItem("GAMMA", probe.Detail, SetupCheckState.Fail);
        }

        if (probe.Kept)
        {
            return new SetupCheckItem("GAMMA", probe.Detail, SetupCheckState.Pass);
        }

        // A discarded ramp on an HDR screen is what Windows documents: gamma is
        // undefined in HDR modes. That is worth knowing rather than fixing, and it
        // is the single most likely reason a screen preset "did nothing".
        HdrReading reading = HdrReading.Unknown;
        try
        {
            reading = _hdr.Read(device);
        }
        catch (Exception ex)
        {
            TraceLog.Write("SETUP hdr read", ex);
        }

        return reading.Known && reading.Active
            ? new SetupCheckItem("GAMMA", "HDR is on, so Windows discards gamma on this screen", SetupCheckState.Warn)
            : new SetupCheckItem("GAMMA", probe.Detail, SetupCheckState.Fail);
    }

    public async Task<SetupCheckItem> Hdr(SetupScreen screen)
    {
        HdrProbeResult result = await _hdr.ProbeAsync(screen.Device, screen.Key, HdrTimeout);

        if (result.Skipped)
        {
            return new SetupCheckItem("HDR", result.Detail, SetupCheckState.Skip);
        }

        if (!result.Ok)
        {
            return new SetupCheckItem("HDR", result.Detail, SetupCheckState.Fail);
        }

        string detail = result.LegacyPath
            ? result.Detail + " (the older Windows call, which cannot tell HDR from advanced colour)"
            : result.Detail;

        return new SetupCheckItem("HDR", detail, SetupCheckState.Pass);
    }

    /// <summary>
    /// Moves the default playback device - and the microphone, when there is a
    /// second one - and puts both back.
    /// <para>
    /// The switch is verified by reading the default back, the same way a slot's
    /// route is. This is the check that was never possible from a unit test: it is
    /// the one that proves the app's own COM policy call works on this machine
    /// rather than only in a log line.
    /// </para>
    /// </summary>
    public IReadOnlyList<SetupCheckItem> Routes()
    {
        List<SetupCheckItem> items = new()
        {
            TestFlow("AUDIO", WindowsAudioFlow.Playback, _devices.GetOutputDevices(), "playback"),
            TestFlow("MIC", WindowsAudioFlow.Recording, _devices.GetInputDevices(), "microphone"),
        };

        return items;
    }

    private SetupCheckItem TestFlow(
        string label,
        WindowsAudioFlow flow,
        IReadOnlyList<AudioDeviceInfo> devices,
        string name)
    {
        string current = _devices.GetDefaultDeviceId(flow.IsRecording(), communications: false);

        if (string.IsNullOrWhiteSpace(current))
        {
            return new SetupCheckItem(label, "Windows did not report a default " + name, SetupCheckState.Skip);
        }

        AudioDeviceInfo? other = null;
        foreach (AudioDeviceInfo device in devices)
        {
            if (device.Id.Length > 0
                && !string.Equals(device.Id, current, StringComparison.OrdinalIgnoreCase))
            {
                other = device;
                break;
            }
        }

        if (other is null)
        {
            // Nothing to switch to is not a broken machine, and switching to the
            // same device would only prove that the call can be a no-op.
            return new SetupCheckItem(
                label,
                "only one " + name + " device is connected",
                SetupCheckState.Skip);
        }

        if (!WindowsAudioRouting.TrySetDefault(flow, other.Id, _devices, out string error))
        {
            return new SetupCheckItem(
                label,
                "Windows would not move the default " + name + " to " + other.Name + " (" + error + ")",
                SetupCheckState.Fail);
        }

        if (!WindowsAudioRouting.TrySetDefault(flow, current, _devices, out error))
        {
            // Said loudly and first: the machine is now on the wrong device.
            return new SetupCheckItem(
                label,
                "moved to " + other.Name + " and could not be moved back (" + error + ")",
                SetupCheckState.Fail);
        }

        string detail = "moved to " + other.Name + " and back";
        if (_fxSoundRunning?.Invoke() == true)
        {
            detail += " (FxSound bypassed for a moment)";
        }

        return new SetupCheckItem(label, detail, SetupCheckState.Pass);
    }
}
