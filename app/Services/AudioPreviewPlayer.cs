using System;
using System.IO;
using System.Windows.Media;

namespace GamerTool.Services;

public sealed class AudioPreviewPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private bool _disposed;
    private bool _playing;
    private bool _loop = true;

    public AudioPreviewPlayer()
    {
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    public bool IsPlaying => _playing;

    /// <summary>Where the track is right now, used to drive the spectrum.</summary>
    public TimeSpan Position
    {
        get
        {
            try
            {
                return _player.Position;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }
    }

    public bool Ready { get; private set; }

    public string? FilePath { get; private set; }

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

    public void Prepare()
    {
        bool resume = _playing;
        if (Ready)
        {
            _player.Stop();
            _player.Close();
            Ready = false;
        }

        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerTool");
            Directory.CreateDirectory(folder);

            string asset = AssetFor(Track);
            string target = Path.Combine(folder, asset);
            byte[] data = DisplayPreview.ReadAsset(asset);
            if (data.Length == 0)
            {
                Failed?.Invoke("PREVIEW TRACK MISSING");
                return;
            }

            if (!File.Exists(target) || new FileInfo(target).Length != data.Length)
            {
                File.WriteAllBytes(target, data);
            }

            _player.Volume = 0.5;
            _player.Open(new Uri(target));
            FilePath = target;
            Ready = true;

            if (resume)
            {
                _player.Play();
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO PREVIEW", ex);
            Failed?.Invoke("PREVIEW TRACK COULD NOT BE OPENED");
        }
    }

    public void Toggle()
    {
        if (_playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        Prepare();
        if (!Ready)
        {
            return;
        }

        try
        {
            if (_player.NaturalDuration.HasTimeSpan && _player.NaturalDuration.TimeSpan > TimeSpan.Zero)
            {
                _player.Position = TimeSpan.Zero;
            }

            _player.Play();
            _playing = true;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
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

        try
        {
            _player.Pause();
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO PREVIEW", ex);
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

        try
        {
            _player.Stop();
            _player.Position = TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO PREVIEW", ex);
        }

        _playing = false;
        StateChanged?.Invoke();
    }

    public void ApplyMix(double masterGain, double balance)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            double span = Math.Clamp((masterGain + 20.0) / 40.0, 0.0, 1.0);
            _player.Volume = 0.15 + (0.85 * span);
            _player.Balance = Math.Clamp(balance / 20.0, -1.0, 1.0);
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO PREVIEW", ex);
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_disposed || !_loop)
        {
            return;
        }

        try
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO PREVIEW", ex);
        }
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        TraceLog.Write("AUDIO PREVIEW FAIL", e.ErrorException);
        _playing = false;
        Failed?.Invoke("PREVIEW TRACK COULD NOT BE PLAYED");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO PREVIEW", ex);
        }
    }
}
