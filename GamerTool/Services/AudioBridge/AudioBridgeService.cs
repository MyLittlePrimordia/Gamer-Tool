using NAudio.CoreAudioApi;
using NAudio.Wave;
using GamerTool.Models;

namespace GamerTool.Services.AudioBridge;

/// <summary>
/// The actual "no signing required" audio engine. Architecture, and why it
/// looks the way it does:
///
/// A naive "loopback-capture the default device, EQ it, play it back to the
/// same device" pipeline would produce an ECHO, not an equalized stream —
/// WASAPI loopback capture taps a COPY of what's already being rendered; it
/// doesn't remove the original from the speakers. So this needs a detour
/// through an intermediate virtual playback device:
///
///   apps/games  --> "CABLE Input" (virtual device, set as Windows default)
///                        |
///                        | WASAPI loopback capture (this class)
///                        v
///                  10-band EQ (GraphicEqProcessor)
///                        v
///                  WASAPI render --> real speakers/headphones
///
/// Only one (processed) copy of the audio ever reaches your ears. The
/// virtual device ("CABLE Input") comes from VB-CABLE, a free, already
/// WHQL-signed virtual audio driver — see VirtualCableInstallerService — so
/// nothing in this pipeline needs an unsigned APO or any driver signing of
/// our own.
///
/// SAFETY: if GamerTool exits (or crashes) while the system default output
/// is pointed at the virtual cable and nothing is running to bridge it
/// onward, the user's audio goes silent system-wide — the same class of
/// failure this whole project exists to avoid, just via a different
/// mechanism than the original APO permission bug. Two things guard against
/// that: MainWindow reverts the default device on every normal shutdown
/// path, and MainWindow_Loaded runs a startup self-heal check that detects
/// "default is the cable, but nothing is bridging it" (e.g. after a crash)
/// and reverts automatically before the user even notices.
/// </summary>
public static class AudioBridgeService
{
    public const string VirtualCableInputNameHint = "CABLE Input";

    private static WasapiLoopbackCapture? _capture;
    private static WasapiOut? _renderer;
    private static GraphicEqProcessor? _eq;
    private static BufferedWaveProvider? _buffer;

    public static bool IsRunning { get; private set; }

    /// <summary>Finds VB-CABLE's playback ("Input") endpoint via NAudio's own
    /// device enumerator, or null if the virtual cable isn't installed.
    /// Separate from DefaultDeviceService's enumerator (AudioSwitcher) since
    /// NAudio's WasapiLoopbackCapture needs its own MMDevice type — keeping
    /// each library's device objects with the library that consumes them
    /// avoids fragile cross-library ID translation.</summary>
    public static MMDevice? FindVirtualCableInput()
    {
        var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(VirtualCableInputNameHint, StringComparison.OrdinalIgnoreCase))
                return device;
            device.Dispose();
        }
        return null;
    }

    /// <summary>The current Windows default render device (NAudio's MMDevice
    /// type), used both to find "what should we render the EQ'd audio to"
    /// and, before switching, "what was the real device we should remember
    /// to restore".</summary>
    public static MMDevice GetCurrentDefaultRenderDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    /// <summary>
    /// Starts the bridge: opens loopback capture on the virtual cable and
    /// renders the EQ'd result to <paramref name="realDevice"/>. Does NOT
    /// itself switch the Windows default device — callers switch default to
    /// the virtual cable first (see MainWindow), so the ordering is explicit
    /// and callers can decide what to do if the switch fails.
    /// </summary>
    public static void Start(MMDevice virtualCableInput, MMDevice realDevice, AudioPreset initialPreset)
    {
        Stop(); // idempotent: tear down any previous pipeline cleanly first

        _capture = new WasapiLoopbackCapture(virtualCableInput);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        _eq = new GraphicEqProcessor(_buffer.ToSampleProvider());
        _eq.UpdatePreset(initialPreset);

        // Shared mode + the capture's own WaveFormat: Windows' audio engine
        // handles the sample-rate/format conversion to whatever the real
        // device's mix format is. This is standard WASAPI shared-mode
        // behavior and covers the overwhelming majority of setups (virtual
        // cable and real device both normally converge on 48kHz float
        // stereo). If you hit a device with an unusual native format, the
        // fix is inserting a NAudio MediaFoundationResampler here.
        _renderer = new WasapiOut(realDevice, AudioClientShareMode.Shared, true, 50);
        _renderer.Init(_eq);

        _capture.StartRecording();
        _renderer.Play();
        IsRunning = true;
    }

    public static void ApplyPreset(AudioPreset preset) => _eq?.UpdatePreset(preset);

    public static void Stop()
    {
        try { _renderer?.Stop(); } catch { /* best effort */ }
        try { _capture?.StopRecording(); } catch { /* best effort */ }

        _renderer?.Dispose();
        _capture?.Dispose();

        _renderer = null;
        _capture = null;
        _eq = null;
        _buffer = null;
        IsRunning = false;
    }
}
