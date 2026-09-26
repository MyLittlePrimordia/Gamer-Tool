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

    public bool Ready { get; private set; }

    public string? FilePath { get; private set; }

    public event Action? StateChanged;

    public event Action<string>? Failed;

    public void Prepare()
    {
        if (Ready)
        {
            return;
        }

        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerTool");
            Directory.CreateDirectory(folder);

            string target = Path.Combine(folder, "preview.mp3");
            byte[] data = DisplayPreview.ReadAsset("preview.mp3");
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
