using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerTool.Services;

/// <summary>
/// Taps whatever the default render device is playing, and hands it on as mono
/// samples. This is the live "ear" the spectrum listens with.
/// <para>
/// Windows will not give an application a copy of what it is playing, so the
/// only way to hear system audio is to ask the endpoint for a loopback: a second
/// client on the same device that receives copies of what is sent to the
/// speakers. That is what FxSound's own analyser does, and it is why this sees
/// games, music and video without the app knowing anything about them.
/// </para>
/// <para>
/// The fiddly part is not the capture, it is the device. The endpoint can
/// vanish mid session, because plugging in headphones changes the Windows
/// default, and a capture bound to the old endpoint then delivers silence
/// forever. So the feed keeps the endpoint id it opened with, notices when the
/// default no longer matches, and reopens against the new one.
/// </para>
/// </summary>
public sealed class LoopbackSampleFeed : ISampleFeed
{
    /// <summary>About a third of a second of mono audio, which is well over one window.</summary>
    private const int RingSamples = 16384;

    /// <summary>
    /// How much audio the engine is asked to buffer before it wakes the capture
    /// thread. Two device periods is enough to absorb a scheduling hiccup
    /// without adding a delay the eye can see.
    /// </summary>
    private const int CaptureBufferMs = 20;

    private readonly float[] _ring = new float[RingSamples];
    private readonly object _ringGate = new();

    /// <summary>Set when capture died and the reason, for the log.</summary>
    public string? LastFailure { get; private set; }

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private string? _endpointId;
    private int _sampleRate = 48000;
    private int _write;

    /// <summary>Raised when the endpoint is gone, so the log can say so once.</summary>
    public event Action<string>? CaptureLost;

    public bool IsLive { get; private set; }

    public int SampleRate => _sampleRate;

    public void Start()
    {
        Open();
    }

    public void Stop()
    {
        lock (_ringGate)
        {
            CloseCapture();
            Array.Clear(_ring);
            _write = 0;
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Latest mono samples, oldest first. Returns how many were written, which
    /// is zero until enough audio has arrived to fill a window.
    /// </summary>
    public int Read(Span<float> into)
    {
        if (into.Length == 0)
        {
            return 0;
        }

        // Checked here rather than only on the capture event, because that event
        // is not the first thing to notice. The default endpoint can change while
        // everything still looks healthy, and the test is free.
        if (IsLive && DefaultEndpointChanged())
        {
            Note("default audio device changed, reopening capture");
            lock (_ringGate)
            {
                CloseCapture();
            }

            Open();
        }

        lock (_ringGate)
        {
            int available = Math.Min(_write, RingSamples);
            if (available == 0)
            {
                return 0;
            }

            int take = Math.Min(available, into.Length);
            int newest = _write;
            int oldest = newest - take;

            for (int i = 0; i < take; i++)
            {
                into[i] = _ring[(oldest + i) % RingSamples];
            }

            return take;
        }
    }

    private void Open()
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();
            MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var capture = new LowLatencyLoopbackCapture(device, CaptureBufferMs);
            WaveFormat format = capture.WaveFormat;

            capture.DataAvailable += OnData;
            capture.StartRecording();

            _device = device;
            _capture = capture;
            _endpointId = device.ID;
            _sampleRate = format.SampleRate;
            IsLive = true;
            LastFailure = null;
        }
        catch (Exception ex)
        {
            // A machine with no render device, a session with no audio, or a
            // policy that forbids it. The spectrum has to keep drawing its rest
            // state, so this is recorded and swallowed rather than thrown.
            Note("could not open a loopback capture: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        byte[]? buffer = args.Buffer;
        if (buffer is null || buffer.Length == 0 || _capture is null)
        {
            return;
        }

        WaveFormat format = _capture.WaveFormat;
        int bytesPerSample = Math.Max(2, format.BitsPerSample / 8);
        int step = bytesPerSample * Math.Max(1, format.Channels);
        int frames = buffer.Length / step;

        lock (_ringGate)
        {
            for (int i = 0; i < frames; i++)
            {
                int offset = i * step;

                // Downmixed to mono here rather than analysed as stereo. Two
                // channels of the same signal averaged give a spectrum that is
                // correct but a little quieter than it looks, and a phase
                // cancelling pair averages to near silence.
                double sum = 0.0;
                for (int c = 0; c < format.Channels; c++)
                {
                    int at = offset + (c * bytesPerSample);
                    if (at + bytesPerSample <= buffer.Length)
                    {
                        sum += BitConverter.ToSingle(buffer, at);
                    }
                }

                _ring[_write % RingSamples] = (float)(sum / Math.Max(1, format.Channels));
                _write++;
            }

            if (_write > int.MaxValue - RingSamples)
            {
                // Long session. Resetting the index is harmless, the ring keeps
                // the most recent samples either way.
                _write = RingSamples;
            }
        }
    }

    private bool DefaultEndpointChanged()
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();
            MMDevice current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return !string.Equals(current.ID, _endpointId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // The enumerator itself failing means the audio stack is in a bad
            // state. Treated as "changed" so the reopen path gets a chance.
            return true;
        }
    }

    private void CloseCapture()
    {
        if (_capture is not null)
        {
            try
            {
                _capture.DataAvailable -= OnData;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception)
            {
                // A capture whose device has already gone throws on teardown.
            }

            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _endpointId = null;
        IsLive = false;
    }

    private void Note(string reason)
    {
        IsLive = false;
        LastFailure = reason;
        TraceLog.Write("SPECTRUM loopback: " + reason);
        CaptureLost?.Invoke(reason);
    }

    /// <summary>
    /// A loopback capture that can be asked for a small buffer.
    /// <para>
    /// NAudio's own <c>WasapiLoopbackCapture</c> hard codes a 100 ms buffer and
    /// offers no way to change it, so the engine hands audio over in 60 ms
    /// lumps. Measured on this machine, a transient took 157 ms to reach the
    /// bars, which reads as the spectrum dragging behind the music. Most of that
    /// was the capture, not the analysis.
    /// </para>
    /// <para>
    /// The loopback flag is the only difference between loopback and a plain
    /// capture, and NAudio already exposes it as a protected virtual on
    /// <see cref="WasapiCapture"/>. Subclassing it means the buffer size is a
    /// constructor argument, which drops the handover to the device period of
    /// 10 ms and the same transient to about 60 ms end to end. No COM of our own
    /// is involved, and it goes through the same NAudio code as before.
    /// </para>
    /// </summary>
    private sealed class LowLatencyLoopbackCapture : WasapiCapture
    {
        public LowLatencyLoopbackCapture(MMDevice device, int latencyMs)
            : base(device, useEventSync: true, audioBufferMillisecondsLength: latencyMs)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
    }
}
