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

    /// <summary>
    /// Set the moment the app starts going away, so the fault handlers can tell a
    /// problem worth showing from teardown noise.
    /// <para>
    /// Process wide rather than a field on the window, because the handlers that
    /// need to read it are static and are reached from threads that have no window
    /// to ask.
    /// </para>
    /// </summary>
    public bool ShuttingDown { get; set; }

    /// <summary>
    /// Cancelled once the app starts going away, so a reset already in flight
    /// stops rather than running on past the exit path.
    /// <para>
    /// Process wide and owned here rather than on the window, because
    /// <see cref="EmergencyReset"/> reaches it from
    /// <c>AppDomain.ProcessExit</c>, where there is no window to ask and the
    /// dispatcher may already be gone.
    /// </para>
    /// <para>
    /// Never disposed. Disposing a source whose token somebody is still holding
    /// turns a later read into an <see cref="ObjectDisposedException"/> on the exit
    /// path, and there is nothing to release: the token and its registration live
    /// for the life of the process either way.
    /// </para>
    /// </summary>
    public CancellationToken AudioStop => _audioStop.Token;

    private readonly CancellationTokenSource _audioStop = new();

    /// <summary>
    /// Stops pending audio work. Safe to call more than once, and safe to call from
    /// a fault handler.
    /// </summary>
    /// <remarks>
    /// Wrapped because it runs first in <see cref="EmergencyReset.Run"/>, before
    /// anything is restored. A cancellation callback that threw would take the
    /// backlight restore down with it, which is the one outcome worse than the
    /// interleaving this exists to prevent.
    /// <para>
    /// Returns nothing, deliberately. It reported whether anything was cancelled,
    /// which no caller read and which is not a question worth asking: the exit is
    /// reached from <c>QuitApp</c>, <c>OnClosing</c>, <c>Application.Exit</c> and
    /// <c>AppDomain.ProcessExit</c>, any of which can be the first to run, so "was
    /// there anything to cancel" is true on some of them by construction. The
    /// question that matters - is the token cancelled now - is answered by reading
    /// <see cref="AudioStop"/>.
    /// </para>
    /// </remarks>
    public void StopPendingAudio()
    {
        try
        {
            _audioStop.Cancel();
        }
        catch (Exception ex)
        {
            TraceLog.Write("AUDIO STOP", ex);
        }
    }

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

        // First, before anything is touched. This is what stops a sound reset that
        // is already in flight from running on past this point.
        //
        // A reset is roughly three quarters of a second of child-process waits, and
        // it ends by powering the engine back on. Quitting inside that window used
        // to interleave two independent --power sequences on an engine that takes
        // one command line at a time - this one on its way to --power=1 while the
        // exit path was already writing the flat preset and powering down. The
        // result was an app that had exited leaving the engine powered back up, or
        // holding whichever of the two curves landed last.
        //
        // Cancelling rather than waiting is deliberate. The reset's step boundaries
        // are all *after* it has powered the engine down, so every one of them
        // leaves the engine off - which is the state the exit path wants and the
        // one it was about to ask for anyway. Nothing is left half done, and the
        // exit does not sit waiting out a delay that exists only to give the engine
        // time to come back up.
        state.StopPendingAudio();

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

                    // The bypass has to come off before the command is built, or it
                    // is built as a bypassed one: ExpectedMasterGain returns 0.0
                    // while EffectsEnabled is false, so quitting with the switch
                    // thrown wrote a flat, silent, bypassed preset rather than
                    // putting the engine back. The engine is powered down on the
                    // next line, so what is left in its preset folder is what the
                    // next launch reads - which makes the state this leaves behind
                    // the one that matters, not the one the user saw a moment ago.
                    //
                    // Forced rather than read back afterwards: this is the exit
                    // path and restoring is its whole purpose. The user's own
                    // bypass state is still in the profile and returns on the next
                    // launch.
                    bool wasBypassed = audio.EffectsEnabled;
                    audio.EffectsEnabled = true;

                    // The curve is written before it is selected, and the gains are
                    // sent as a command of their own, for the same reason the reset
                    // does it: selecting a preset makes the engine apply that file's
                    // state after it finishes parsing the command line. Quitting
                    // with only BuildApplyCommand selected a GamerTool.fac that
                    // still held the last tune, so the engine was handed that
                    // curve on the way out - the one moment where leaving a game's
                    // equaliser in place is least wanted and least noticed.
                    bool wroteCurve = FxPresetFile.Write(
                        flat,
                        AudioService.BandFrequencies(flat),
                        true) is not null;

                    IReadOnlyList<string> reset = audio.BuildApplyCommands(
                        flat,
                        string.Empty,
                        wroteCurve);

                    // The short budget, deliberately. This runs from the exit path
                    // with no window left to show a wait, and it now makes more
                    // calls than it used to, so at the normal budget quitting would
                    // hang for seconds after the last pixel had gone. The engine is
                    // started on its way out either way; how long it takes to
                    // accept the command is not worth the user watching nothing
                    // happen.
                    try
                    {
                        foreach (string command in reset)
                        {
                            audio.Run(command, ShutdownWaitMs);
                        }
                    }
                    finally
                    {
                        audio.EffectsEnabled = wasBypassed;
                    }

                    audio.Run("--power=0", ShutdownWaitMs);
                }
            }

            TraceLog.Write("RESET DONE");
        }
        catch (Exception ex)
        {
            TraceLog.Write("RESET", ex);
        }

        // Last, and only if there is anything to undo. The preset file is the
        // last thing this app needs in FxSound's folder: it exists so the engine
        // can be handed an equaliser curve, and by the time the process is going
        // away nothing is being applied any more. Leaving it behind would mean
        // writing into another program's data directory and staying there.
        //
        // Gated on the same flags as the reset above so a launch where the user
        // never touched anything does not go poking in a folder it never wrote
        // to, and last so the audio has already been put back first in the
        // vanishingly unlikely case the engine wants the file to still be there.
        if (state.DisplayTouched || state.AudioTouched)
        {
            FxPresetFile.RemoveWrittenPreset();
        }
    }
}
