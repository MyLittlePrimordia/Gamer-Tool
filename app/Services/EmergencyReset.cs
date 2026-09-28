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

    /// <summary>
    /// Puts every display's backlight back where it was found. Kept beside the
    /// reset rather than inside it because a monitor left at 5% because the app
    /// died is its own kind of bad outcome, and the gamma ramp being flat does
    /// not fix that.
    /// </summary>
    public Action? RestoreBacklight { get; set; }

    public static SessionState Current { get; } = new();

    private int _done;

    public bool TryBeginReset()
    {
        return Interlocked.Exchange(ref _done, 1) == 0;
    }
}

public static class EmergencyReset
{
    /// <summary>Per-call budget for the exit path. See <see cref="AudioService"/>.</summary>
    private const int ShutdownWaitMs = 1200;

    public static void Run()
    {
        SessionState state = SessionState.Current;

        // The backlight goes back first and on its own account. Whether the gamma
        // ramp or the audio were ever touched is beside the point here: a display
        // this app dimmed has to come back whatever else happened.
        try
        {
            state.RestoreBacklight?.Invoke();
        }
        catch (Exception ex)
        {
            TraceLog.Write("BACKLIGHT RESTORE", ex);
        }

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

                    // The short budget, deliberately. This runs from the exit path
                    // with no window left to show a wait, and it makes two calls,
                    // so at the normal budget quitting would hang for seconds
                    // after the last pixel had gone. The engine is started on its
                    // way out either way; how long it takes to accept the command
                    // is not worth the user watching nothing happen.
                    audio.Run(audio.BuildApplyCommand(flat, string.Empty), ShutdownWaitMs);
                    audio.Run("--power=0", ShutdownWaitMs);
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
