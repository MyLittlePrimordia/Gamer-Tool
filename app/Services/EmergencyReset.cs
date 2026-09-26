using System;
using System.Threading;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class SessionState
{
    public bool DisplayTouched { get; set; }

    public bool AudioTouched { get; set; }

    public DisplayService? Display { get; set; }

    public AudioService? Audio { get; set; }

    public static SessionState Current { get; } = new();

    private int _done;

    public bool TryBeginReset()
    {
        return Interlocked.Exchange(ref _done, 1) == 0;
    }
}

public static class EmergencyReset
{
    public static void Run()
    {
        SessionState state = SessionState.Current;
        if (!state.DisplayTouched && !state.AudioTouched)
        {
            return;
        }

        if (!state.TryBeginReset())
        {
            return;
        }

        try
        {
            if (state.DisplayTouched)
            {
                (state.Display ?? new DisplayService()).Reset();
            }

            if (state.AudioTouched)
            {
                AudioService audio = state.Audio ?? new AudioService();
                if (audio.IsInstalled)
                {
                    AudioPreset flat = AudioPreset.Flat();
                    audio.Run(audio.BuildApplyCommand(flat, string.Empty));
                    audio.Run("--power=0");
                }
            }

            TraceLog.Write("RESET DONE");
        }
        catch (Exception ex)
        {
            TraceLog.Write("RESET", ex);
        }
    }
}
