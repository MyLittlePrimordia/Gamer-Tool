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

    /// <summary>
    /// How long the capture may go without delivering a single buffer before it
    /// is treated as having stopped.
    /// <para>
    /// Generous against the capture period, which is
    /// <see cref="CaptureBufferMs"/>, because the handover is a scheduler's
    /// business and a loaded machine can stretch one gap well past its nominal
    /// length. It is still far below the point where a held frame reads as a
    /// frozen one, because a frozen frame is the failure being guarded against.
    /// </para>
    /// </summary>
    private const int StaleMs = 150;

    private readonly float[] _ring = new float[RingSamples];
    private readonly object _ringGate = new();

    /// <summary>
    /// When a buffer last arrived, so a capture that has gone quiet can be told
    /// apart from one that is merely between buffers.
    /// <para>
    /// Read on every frame and written on the capture thread, so it goes through
    /// <see cref="Interlocked"/> rather than the ring lock: the staleness question
    /// has to be answerable without taking a lock the capture thread is
    /// contending for, or a stalled capture could keep the very lock that would
    /// let it be noticed.
    /// </para>
    /// </summary>
    private long _lastDataAt;

    /// <summary>
    /// When the current capture was opened, so a capture that never delivers its
    /// first buffer is still recognised as having had its chance.
    /// </summary>
    private long _openedAt;

    /// <summary>Set when capture died and the reason, for the log.</summary>
    public string? LastFailure { get; private set; }

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private string? _endpointId;
    private int _sampleRate = 48000;
    private int _write;

    /// <summary>
    /// The layout of the samples <see cref="OnData"/> is being handed, held apart
    /// from the capture it came from.
    /// <para>
    /// The audio callback used to reach through to <c>_capture.WaveFormat</c>,
    /// and that was a live <see cref="ObjectDisposedException"/> waiting to happen.
    /// The callback runs on NAudio's capture thread while the UI thread can be
    /// closing the very capture it belongs to - which is what happens when the
    /// default output device changes, the exact case this class exists to survive
    /// - and it read the field without the lock the close happens under. It could
    /// pass the null check and then read a format property off an object that had
    /// already been disposed, on a thread with no handler of its own.
    /// </para>
    /// <para>
    /// A capture's format cannot change over its lifetime, so it is captured once
    /// when the capture is opened and read once per callback. A reference read and
    /// a reference write are each atomic, so the callback needs no lock to get a
    /// consistent object: it either sees the format belonging to this capture or
    /// sees null because the capture has already been closed, and in the second
    /// case there is nothing to interpret anyway.
    /// </para>
    /// </summary>
    private WaveFormat? _format;

    /// <summary>Raised when the endpoint is gone, so the log can say so once.</summary>
    public event Action<string>? CaptureLost;

    public bool IsLive { get; private set; }

    public int SampleRate => _sampleRate;

    public void Start()
    {
        lock (_ringGate)
        {
            // Asked for on every window state change, every visibility change and
            // every page switch, so it arrives far more often than the capture
            // needs opening. Opening again each time was leaving the previous
            // capture running and undisposed: a second WASAP client on the same
            // endpoint, its device leaked, and its callback still writing into
            // this ring behind the new one's back. So starting is a question, and
            // the answer is only acted on when capture is not already running.
            if (_capture is not null)
            {
                return;
            }
        }

        Open();
    }

    public void Stop() => Close();

    public void Dispose() => Close();

    /// <summary>
    /// Tears the capture down and empties the ring.
    /// <para>
    /// The dispose deliberately happens outside the ring lock. NAudio's
    /// WasapiCapture.Dispose stops recording and then joins its capture thread,
    /// and that thread raises DataAvailable into <see cref="OnData"/>, which
    /// takes this same lock. Holding the lock across the join is a deadlock: the
    /// only thread that can release it is the one being waited on. Nothing
    /// reports it - no exception, no log line, no recovery - because the UI
    /// thread simply never comes back.
    /// </para>
    /// <para>
    /// So the lock is taken only to take ownership of the capture and to empty
    /// the ring, and the blocking calls happen afterwards on this thread. The
    /// fields are nulled while the lock is held so a second close arriving in
    /// that window finds nothing to do rather than disposing twice.
    /// </para>
    /// </summary>
    private void Close()
    {
        WasapiCapture? capture;
        MMDevice? device;

        lock (_ringGate)
        {
            capture = _capture;
            device = _device;
            _capture = null;
            _device = null;
            _endpointId = null;

            // Under the same lock as the empty below, so a buffer that is already
            // on its way in either lands first and is cleared, or arrives after
            // IsLive has come down and is ignored by OnData. Anything else would
            // leave the ring holding a tail of the device that just went away,
            // which is exactly what ClearRing exists to prevent.
            IsLive = false;
            _format = null;
            ClearRing();
        }

        DisposeProbe?.Invoke();

        if (capture is null)
        {
            return;
        }

        try
        {
            capture.DataAvailable -= OnData;
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception)
        {
            // A capture whose device has already gone throws on teardown.
        }

        device?.Dispose();
    }

    /// <summary>
    /// Whether the calling thread is inside the ring lock right now.
    /// <para>
    /// This is the invariant the deadlock turned on, and it is the only one worth
    /// asserting about a teardown that cannot be run for real: reproducing the
    /// hang needs a live WASAPI capture mid-callback, and a test that depends on
    /// one either passes vacuously on CI or hangs the suite. Asking whether the
    /// lock is held at the point the dispose happens is deterministic, needs no
    /// sound card, and fails loudly if the dispose is ever moved back inside the
    /// lock.
    /// </para>
    /// </summary>
    internal bool RingLockHeldByThisThread => Monitor.IsEntered(_ringGate);

    /// <summary>
    /// Runs at the point in <see cref="Close"/> where the capture would be
    /// disposed, so a test can inspect the state at exactly that moment.
    /// </summary>
    internal Action? DisposeProbe { get; set; }

    /// <summary>
    /// Empties the ring and the write index, so the next capture starts from
    /// silence rather than from whatever the last device was playing.
    /// <para>
    /// The index is the important half. The ring is a fixed length and the
    /// analysis asks for the newest window each frame, so leaving the index where
    /// a previous device left it is what makes a reopen replay the old device's
    /// audio: the window is read out of positions that nothing has written to
    /// yet, and those positions still hold the samples the dead endpoint sent.
    /// </para>
    /// </summary>
    private void ClearRing()
    {
        Array.Clear(_ring);
        _write = 0;
    }

    /// <summary>
    /// Latest mono samples, oldest first. Returns how many were written, which
    /// is zero until enough audio has arrived to fill a window.
    /// <para>
    /// The window is deliberately not consumed: the ring holds the most recent
    /// third of a second and the analyser wants the newest slice of it every
    /// frame. That is fine while the capture is delivering, and it is why a
    /// capture that quietly stops needs handling rather than leaving alone. With
    /// nothing arriving, the same samples come back for ever, the view sees a
    /// full window on every frame, the levels it computes are the levels of
    /// whatever was playing when the capture died, and the bars sit at those
    /// heights instead of falling to silence - a meter stuck on a peak. So the
    /// ring is emptied the moment a capture is found to have stopped, and the
    /// zero that follows is what lets the view decay the bars.
    /// </para>
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
            Close();
            Open();
        }

        // The other way a capture dies is quietly. Nothing raises an event, the
        // default endpoint never changes, and the only symptom is that no buffer
        // ever arrives again. Left alone that is the frozen spectrum described on
        // this method: the same window, redrawn sixty times a second, looking for
        // all the world like a meter stuck on a peak.
        if (IsLive && CaptureHasGoneQuiet())
        {
            Note("loopback stopped delivering audio, reopening capture");
            Close();
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

    /// <summary>
    /// True when the capture claims to be live but has not handed over a buffer
    /// for longer than <see cref="StaleMs"/>.
    /// <para>
    /// The arrival time is what decides it rather than the write index, because
    /// the write index moves on the capture thread and asking whether it has
    /// moved is only the same question asked late. This one is also usable
    /// before the first buffer lands, which is the case of a capture that opens
    /// and then never delivers anything at all.
    /// </para>
    /// </summary>
    private bool CaptureHasGoneQuiet()
    {
        long last = Interlocked.Read(ref _lastDataAt);
        if (last == 0)
        {
            // Opened, and still nothing. A capture that never delivers its first
            // buffer is dead, and this is only reached once the grace period has
            // already passed, because opening happened on an earlier frame.
            return Environment.TickCount64 - _openedAt > StaleMs;
        }

        return Environment.TickCount64 - last > StaleMs;
    }

    private void Open()
    {
        MMDevice? device = null;
        try
        {
            using MMDeviceEnumerator enumerator = new();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var capture = new LowLatencyLoopbackCapture(device, CaptureBufferMs);
            WaveFormat format = capture.WaveFormat;

            capture.DataAvailable += OnData;
            capture.StartRecording();

            _device = device;
            _capture = capture;
            _format = format;
            _endpointId = device.ID;
            _sampleRate = format.SampleRate;
            IsLive = true;
            LastFailure = null;

            // Both stamps restart with the capture, so a reopen is not judged
            // against a previous device's timing and a reopened capture gets its
            // full grace period before it counts as quiet.
            Interlocked.Exchange(ref _lastDataAt, 0);
            Interlocked.Exchange(ref _openedAt, Environment.TickCount64);

            // Held now rather than only on the field, because the local that
            // created it must not also be the thing that disposes it. A throw
            // between here and the assignment above would otherwise leave a
            // device open with nothing holding a reference to it.
            device = null;
        }
        catch (Exception ex)
        {
            // A machine with no render device, a session with no audio, or a
            // policy that forbids it. The spectrum has to keep drawing its rest
            // state, so this is recorded and swallowed rather than thrown.
            Note("could not open a loopback capture: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            device?.Dispose();
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        byte[]? buffer = args.Buffer;
        WaveFormat? format = _format;
        if (buffer is null || buffer.Length == 0 || format is null)
        {
            return;
        }

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

        // Stamped outside the lock, on the way out, so the read side can ask
        // whether anything has arrived without taking a lock this thread holds
        // sixty times a second.
        Interlocked.Exchange(ref _lastDataAt, Environment.TickCount64);
    }

    private bool DefaultEndpointChanged()
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();

            // Disposed, because this runs on every frame the spectrum draws. An
            // MMDevice holds a COM reference to the endpoint, and sixty of those
            // a minute left for the finalizer is several thousand an hour, all
            // opened on the UI thread and all still live while it happened.
            using MMDevice current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return !string.Equals(current.ID, _endpointId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // The enumerator itself failing means the audio stack is in a bad
            // state. Treated as "changed" so the reopen path gets a chance.
            return true;
        }
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
