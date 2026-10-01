using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GamerTool.Services;

/// <summary>
/// Plays the three preview loops out of the executable, with nothing written to
/// disk.
/// <para>
/// It used to be WPF's <c>MediaPlayer</c>, which only accepts a URI to a real
/// file and has no stream overload. So the embedded bytes were copied out to
/// <c>%LOCALAPPDATA%\GamerTool</c> on every launch, from the window
/// constructor, and never deleted: eleven megabytes of litter written before the
/// user had even opened a tab, in an app whose whole pitch is that it leaves
/// nothing behind but one file.
/// </para>
/// <para>
/// NAudio is already a dependency for the spectrum, and <c>Mp3FileReader</c>
/// takes a <see cref="Stream"/>, so the audio can be decoded straight out of the
/// embedded resource with no temporary file at all. The output device is opened
/// lazily on the first play rather than at construction, which also stops the
/// app holding an audio endpoint open while nobody is previewing anything.
/// </para>
/// <para>
/// Gain and balance are applied in the sample chain rather than on the output,
/// because <c>WasapiOut</c> exposes no per-channel balance. That is arithmetic
/// over floats, so it lives in <see cref="PreviewMixProvider"/> where it can be
/// tested without a sound card - see <c>PreviewMixProviderTests</c>.
/// </para>
/// </summary>
public sealed class AudioPreviewPlayer : IDisposable
{
    private readonly object _gate = new();

    private Mp3FileReader? _reader;
    private PreviewMixProvider? _mix;
    private WasapiOut? _out;

    private bool _disposed;
    private bool _playing;

    /// <summary>Set while the app asked for a stop, so the loop knows not to restart.</summary>
    private bool _stopping;

    public bool IsPlaying => _playing;

    public bool Ready { get; private set; }

    public event Action? StateChanged;

    public event Action<string>? Failed;

    /// <summary>
    /// Which of the two preview loops is loaded. A game preset and a music preset
    /// can pull the same curve in opposite directions at the extremes, so the only
    /// honest way to hear one is against both kinds of material.
    /// </summary>
    public enum PreviewTrack
    {
        Game,
        Music,
        Footsteps
    }

    /// <summary>The loop currently loaded.</summary>
    public PreviewTrack Track { get; private set; } = PreviewTrack.Game;

    /// <summary>Raised when the loaded loop changes, so the page can restyle its toggle.</summary>
    public event Action? TrackChanged;

    /// <summary>Switches loop, keeping playback running across the change where possible.</summary>
    public void SetTrack(PreviewTrack track)
    {
        if (track == Track)
        {
            return;
        }

        Track = track;
        TrackChanged?.Invoke();
        Prepare();
    }

    /// <summary>The next loop in the cycle, so one button steps through all three samples.</summary>
    public PreviewTrack NextTrack() => Track switch
    {
        PreviewTrack.Game => PreviewTrack.Music,
        PreviewTrack.Music => PreviewTrack.Footsteps,
        _ => PreviewTrack.Game
    };

    private static string AssetFor(PreviewTrack track) => track switch
    {
        PreviewTrack.Music => "music.mp3",
        PreviewTrack.Footsteps => "footsteps.mp3",
        _ => "game.mp3"
    };

    /// <summary>
    /// Where the track is right now, worked out from the samples actually handed
    /// to the device rather than from the reader's own cursor.
    /// <para>
    /// The reader runs ahead of playback, because the output pulls from it as
    /// fast as the device drains, so the reader's position overshoots by
    /// whatever is buffered. Counting what has gone out the other end is the only
    /// figure that describes the sound the user is hearing.
    /// </para>
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                PreviewMixProvider? mix = _mix;
                if (mix is null || mix.WaveFormat.SampleRate == 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds(
                    (double)mix.SamplesPlayed / mix.WaveFormat.SampleRate);
            }
        }
    }

    /// <summary>
    /// Decodes the current loop into memory and stops playing.
    /// <para>
    /// Called at construction, so it must be cheap and must not open an audio
    /// device. Only the decode happens here; the endpoint is opened by
    /// <see cref="Play"/>.
    /// </para>
    /// </summary>
    public void Prepare()
    {
        bool resume = _playing;

        TearDown();

        Ready = false;
        _playing = false;

        try
        {
            byte[] data = DisplayPreview.ReadAsset(AssetFor(Track));
            if (data.Length == 0)
            {
                Failed?.Invoke("PREVIEW TRACK MISSING");
                return;
            }

            _reader = new Mp3FileReader(new MemoryStream(data, writable: false));
            _mix = new PreviewMixProvider(_reader.ToSampleProvider())
            {
                Volume = 0.5f
            };

            Ready = true;

            if (resume)
            {
                StartPlayback();
            }
        }
        catch (Exception ex)
        {
            TearDown();
            TraceLog.Write("AUDIO PREVIEW", ex);
            Failed?.Invoke("PREVIEW TRACK COULD NOT BE OPENED");
        }
    }

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        // Decoded on demand rather than trusting a decode from startup: a
        // machine that suspended, or a device that was unplugged, can leave a
        // reader built against an endpoint that no longer exists.
        if (!Ready)
        {
            Prepare();
        }

        if (!Ready)
        {
            return;
        }

        try
        {
            if (_playing && _out is not null)
            {
                Restart();
                return;
            }

            StartPlayback();
        }
        catch (Exception ex)
        {
            TearDown();
            TraceLog.Write("AUDIO PREVIEW", ex);
            Failed?.Invoke("PREVIEW TRACK COULD NOT START");
        }
    }

    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            // Paused rather than torn down, so resuming does not re-decode eleven
            // megabytes of MP3 and re-open the endpoint for a button press.
            try
            {
                _out?.Pause();
            }
            catch (Exception ex)
            {
                TraceLog.Write("AUDIO PREVIEW", ex);
            }
        }

        _playing = false;
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        TearDown();
        Ready = false;
        _playing = false;
        StateChanged?.Invoke();
    }

    public void ApplyMix(double masterGain, double balance)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            PreviewMixProvider? mix = _mix;
            if (mix is null)
            {
                return;
            }

            // The same mapping the old MediaPlayer path used, kept deliberately:
            // the audible result of a given master gain should not change because
            // the playback engine underneath it did.
            double span = Math.Clamp((masterGain + 20.0) / 40.0, 0.0, 1.0);
            mix.Volume = (float)(0.15 + (0.85 * span));
            mix.Balance = (float)Math.Clamp(balance / 20.0, -1.0, 1.0);
        }
    }

    /// <summary>
    /// Starts, or restarts from the top, on a freshly opened endpoint.
    /// </summary>
    private void StartPlayback()
    {
        Mp3FileReader? reader = _reader;
        PreviewMixProvider? mix = _mix;
        if (reader is null || mix is null)
        {
            return;
        }

        // Shared mode, deliberately. This is the same path any ordinary player
        // takes, which is what lets FxSound process the loop the way it processes
        // everything else - the entire reason the preview exists. Exclusive mode
        // would seize the endpoint and silence the system, and would also bypass
        // the very engine the user is trying to hear.
        WasapiOut output = new(AudioClientShareMode.Shared, 200);

        // Only the mix is initialised. It already wraps the reader as its source,
        // and handing the reader to the output as well would open a second,
        // competing path to the same bytes.
        output.Init(mix);

        // The handler is attached per output instance and detached in TearDown, so
        // a disposed endpoint cannot raise into a player that has already moved
        // on to a different loop.
        output.PlaybackStopped += OnPlaybackStopped;
        _out = output;

        _stopping = false;
        output.Play();
        _playing = true;
        StateChanged?.Invoke();
    }

    private void Restart()
    {
        lock (_gate)
        {
            try
            {
                _out?.Stop();
            }
            catch (Exception ex)
            {
                TraceLog.Write("AUDIO PREVIEW", ex);
            }

            // The sample chain is single pass, so the loop is a fresh chain over
            // the same in-memory bytes rather than a seek.
            TearDownOutputOnly();

            byte[] data = DisplayPreview.ReadAsset(AssetFor(Track));
            _reader = new Mp3FileReader(new MemoryStream(data, writable: false));
            _mix = new PreviewMixProvider(_reader.ToSampleProvider()) { Volume = 0.5f };

            StartPlayback();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || _stopping)
        {
            return;
        }

        // A device that vanished mid-loop raises this too, and without the
        // exception check the loop would spin rebuilding a chain that can never
        // open, on a timer, forever.
        if (e.Exception is not null)
        {
            _playing = false;
            TraceLog.Write("AUDIO PREVIEW FAIL", e.Exception);
            Failed?.Invoke("PREVIEW TRACK COULD NOT BE PLAYED");
            StateChanged?.Invoke();
            return;
        }

        try
        {
            Restart();
        }
        catch (Exception ex)
        {
            _playing = false;
            TraceLog.Write("AUDIO PREVIEW", ex);
            StateChanged?.Invoke();
        }
    }

    private void TearDownOutputOnly()
    {
        if (_out is not null)
        {
            _out.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _out.Stop();
            }
            catch (Exception ex)
            {
                TraceLog.Write("AUDIO PREVIEW", ex);
            }

            _out.Dispose();
            _out = null;
        }
    }

    private void TearDown()
    {
        lock (_gate)
        {
            _stopping = true;
            TearDownOutputOnly();
            _mix?.Dispose();
            _mix = null;
            _reader?.Dispose();
            _reader = null;
            _stopping = false;
        }

        _playing = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TearDown();
    }

    /// <summary>
    /// Deletes the copies an older build extracted to
    /// <c>%LOCALAPPDATA%\GamerTool</c>.
    /// <para>
    /// Streaming from the embedded resource stops new litter but does nothing
    /// about the eleven megabytes already sitting on the drives of anyone who has
    /// run a previous build, and those files are named after the app so nothing
    /// else will ever clean them up. Run once at startup, best effort, and never
    /// allowed to fail the launch - this is housekeeping, not a step the app
    /// depends on.
    /// </para>
    /// </summary>
    public static void RemoveExtractedCopies()
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerTool");

            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string name in new[] { "game.mp3", "music.mp3", "footsteps.mp3" })
            {
                string path = Path.Combine(folder, name);

                // Only the three names this app ever wrote. The folder may hold a
                // log, and this must never be a wildcard delete in a directory the
                // user can see.
                if (File.Exists(path))
                {
                    File.Delete(path);
                    TraceLog.Write("AUDIO PREVIEW removed extracted copy of " + name);
                }
            }
        }
        catch (Exception ex)
        {
            // A file held open by a previous run that has not fully exited, or a
            // permissions problem on the profile folder. Neither is worth a
            // message on a launch that is otherwise fine.
            TraceLog.Write("AUDIO PREVIEW could not remove extracted copies", ex);
        }
    }
}

/// <summary>
/// Applies the preview's volume and stereo balance to the samples on their way
/// to the device.
/// <para>
/// Done here rather than on the output because <c>WasapiOut</c> has no
/// per-channel balance control, and because this way it is arithmetic over
/// floats in a class with no audio device in it - so the panning can be tested
/// on a build machine with no sound card, which is the only kind of machine CI
/// has.
/// </para>
/// <para>
/// The balance curve is the usual one: full on the side that is not being pulled
/// back, and a straight linear fade to silence on the other at full deflection.
/// At the centre both sides are untouched, so the default mix is bit-for-bit
/// what the track was mastered as.
/// </para>
/// </summary>
public sealed class PreviewMixProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public PreviewMixProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>How many sample frames have been handed to the device.</summary>
    public long SamplesPlayed { get; private set; }

    /// <summary>Linear gain, 0 to 1.</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>Stereo position, -1 hard left to 1 hard right.</summary>
    public float Balance { get; set; }

    public void Dispose()
    {
        // ISampleProvider does not derive from IDisposable, so this is a cast
        // that can fail rather than a call the compiler guarantees.
        if (_source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        int read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            return read;
        }

        SamplesPlayed += read / Math.Max(1, WaveFormat.Channels);

        float gain = Math.Clamp(Volume, 0.0f, 1.0f);
        float balance = Math.Clamp(Balance, -1.0f, 1.0f);

        if (gain == 1.0f && balance == 0.0f)
        {
            return read;
        }

        // Mono is not balanced against anything, so it only gets the gain. A mono
        // track sent through a stereo panner would otherwise have one silent side,
        // which is a phase change nobody asked for.
        if (WaveFormat.Channels != 2)
        {
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] *= gain;
            }

            return read;
        }

        float left = 1.0f;
        float right = 1.0f;

        if (balance > 0.0f)
        {
            right = 1.0f - balance;
        }
        else if (balance < 0.0f)
        {
            left = 1.0f + balance;
        }

        for (int i = 0; i + 1 < read; i += 2)
        {
            buffer[offset + i] *= gain * left;
            buffer[offset + i + 1] *= gain * right;
        }

        return read;
    }
}
