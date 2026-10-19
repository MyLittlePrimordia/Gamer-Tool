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
    /// The engine as the user had it before this app touched it, or null if that
    /// was never known.
    /// <para>
    /// Taken by <see cref="CaptureFxBefore"/> before the first thing this app does
    /// to FxSound, and read on the way out to decide whether the user gets their
    /// sound back rather than a flat curve.
    /// </para>
    /// <para>
    /// Null is the common case and is not a failure. The engine may not have been
    /// running, may not have reported its power state, or may have left only a
    /// status file this app will not trust - and none of those is knowledge. The
    /// exit path falls back to what it has always done, which is at least not worse
    /// than before.
    /// </para>
    /// </summary>
    public FxSnapshot? FxBefore { get; set; }

    /// <summary>
    /// Records the engine's current state, once, before this app changes anything.
    /// <para>
    /// First capture wins, and that is the whole point. A second capture would
    /// record this app's own work as the user's preference - by the time a preset
    /// has been pushed once, the engine's selected preset is
    /// <see cref="FxPresetFile.PresetName"/>, and restoring that on exit would mean
    /// quitting with a game's equaliser still on the screen. That is the single
    /// outcome this feature exists to prevent.
    /// </para>
    /// <para>
    /// Safe to call more than once, safe to call with nothing, and never throws.
    /// It is reached from the apply path, and an exception here would take the
    /// apply down with it.
    /// </para>
    /// </summary>
    public void CaptureFxBefore(AudioService? audio)
    {
        if (FxBefore is not null || audio is null)
        {
            return;
        }

        try
        {
            FxSoundState? state = audio.ReadState(force: true);

            // Not reporting its power state means the status file was absent or
            // stale. Recording what is left would be recording a guess, and a guess
            // on the exit path is worse than the old behaviour it replaces.
            if (state is null || !state.ReportsPower)
            {
                return;
            }

            FxBefore = new FxSnapshot(state.Power, state.SelectedPreset, state.SelectedOutput);
        }
        catch (Exception ex)
        {
            TraceLog.Write("FX SNAPSHOT", ex);
        }
    }

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
    /// <summary>
    /// The commands that hand the user's own engine state back, in order. The
    /// preset goes first and power second, because selecting while the engine is
    /// off does nothing. The preset name is quoted: it came from FxSound's status
    /// file, and "Bass Boost" unquoted is two arguments, which made restoring any
    /// preset with a space in its name silently select nothing.
    /// </summary>
    internal static IReadOnlyList<string> BuildRestoreCommands(FxSnapshot snapshot) => new[]
    {
        AudioService.PresetArgument(snapshot.Preset),
        "--power=1",
    };

    /// <summary>
    /// Whether the engine should be handed back what the user had, rather than
    /// flattened and powered down.
    /// <para>
    /// A snapshot that cannot be trusted falls through to the old behaviour, and
    /// the old behaviour is still correct for a machine that only has FxSound
    /// because this app put it there.
    /// </para>
    /// <para>
    /// Three things have to hold, and the third is the important one.
    /// </para>
    /// <para>
    /// The engine must have been powered on. If the user had it switched off, they
    /// chose that, and restoring into it would be this app deciding otherwise on
    /// the way out.
    /// </para>
    /// <para>
    /// There must be a preset that is plausibly the user's own. No preset selected
    /// means there is nothing to hand back, and the flat reset is the honest
    /// outcome rather than an invented one.
    /// </para>
    /// <para>
    /// And the preset must not be this app's. If the process died while a game
    /// preset was loaded, the engine's selected preset is
    /// <see cref="FxPresetFile.PresetName"/> - restoring that would mean quitting
    /// with a game's equaliser still on the screen. Case insensitively, because the
    /// name comes out of a file another program wrote.
    /// </para>
    /// <para>
    /// The output device is deliberately not part of it. Requiring one as well
    /// would mean every user on the engine's default device - which is most of them
    /// - quietly stopped getting their sound back.
    /// </para>
    /// </summary>
    public static bool ShouldRestoreUserEngine(FxSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.Power)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Preset))
        {
            return false;
        }

        return !string.Equals(
            snapshot.Preset,
            FxPresetFile.PresetName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shorter budget used on the way out, where nothing is left to show a wait
    /// and the process is about to be torn down. At the full budget - the 4000ms
    /// <c>AudioService.ExitWaitMs</c> a normal call gets - quitting could sit there
    /// for eight seconds with no window on screen and no sign anything was happening.
    /// </summary>
    /// <remarks>
    /// Owned here rather than on <see cref="AudioService"/>, and that is a correction
    /// rather than a preference. This constant used to be declared in both classes,
    /// byte for byte, under the same doc comment - and the copy on the service was
    /// referenced by nothing at all. All four real uses are below. So the second
    /// declaration was not a shared budget, it was a dead constant that looked exactly
    /// like the live one, which is the kind of thing somebody tunes one day and
    /// wonders why nothing changed.
    /// </remarks>
    private const int ShutdownWaitMs = 1200;

    /// <summary>Set once the backlight has been put back, so a second exit hook does not repeat it.</summary>
    private static int _backlightRestored;

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
        //
        // Run is registered on both Application.Exit and ProcessExit, so it can
        // execute twice in one process. The backlight restore has its own
        // once-only flag for that reason: the guard further down only covers the
        // work after it.
        if (Interlocked.Exchange(ref _backlightRestored, 1) == 0)
        {
            try
            {
                state.RestoreBacklight?.Invoke();
            }
            catch (Exception ex)
            {
                TraceLog.Write("BACKLIGHT RESTORE", ex);
            }
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
                    // The user's own engine, handed back, if we ever recorded one
                    // that can be trusted. Checked before the flat reset rather
                    // than after, because the reset writes a GamerTool.fac and
                    // selects it: by the time that has happened the evidence of what
                    // the user had is gone.
                    FxSnapshot? restore = ShouldRestoreUserEngine(state.FxBefore)
                        ? state.FxBefore
                        : null;

                    // The bypass has to come off before any command is built, or it
                    // is built as a bypassed one: ExpectedMasterGain returns 0.0
                    // while EffectsEnabled is false, so quitting with the switch
                    // thrown wrote a flat, silent, bypassed preset rather than
                    // putting the engine back. The engine's state on disk is what
                    // the next launch reads - which makes the state this leaves
                    // behind the one that matters, not the one the user saw a
                    // moment ago.
                    //
                    // Forced rather than read back afterwards: this is the exit path
                    // and restoring is its whole purpose. The user's own bypass
                    // state is still in the profile and returns on the next launch.
                    bool wasBypassed = audio.EffectsEnabled;
                    audio.EffectsEnabled = true;

                    try
                    {
                        if (restore is not null)
                        {
                            // Nothing written. The flat path has to write a preset
                            // file because the documented way to push an equaliser
                            // is to write one and select it; here there is a curve
                            // to put back rather than invent, so the file this app
                            // owns is deleted afterwards and the user's file is
                            // what remains selected. Power comes on second, because
                            // selecting while the engine is off does nothing.
                            foreach (string command in BuildRestoreCommands(restore))
                            {
                                audio.Run(command, ShutdownWaitMs);
                            }

                            TraceLog.Write("FX RESTORED " + restore.Preset);
                        }
                        else
                        {
                            // The original exit behaviour, and still the right
                            // answer for a machine that only has FxSound because
                            // this app installed it.
                            AudioPreset flat = AudioPreset.Flat();

                            // The curve is written before it is selected, and the
                            // gains are sent as a command of their own: selecting a
                            // preset makes the engine apply that file's state after
                            // it finishes parsing the command line. Quitting with
                            // only BuildApplyCommands selected a GamerTool.fac that
                            // still held the last tune, so the engine was handed
                            // that curve on the way out.
                            bool wroteCurve = FxPresetFile.Write(
                                flat,
                                AudioService.BandFrequencies(flat),
                                true) is not null;

                            foreach (string command in audio.BuildApplyCommands(
                                flat,
                                string.Empty,
                                wroteCurve))
                            {
                                audio.Run(command, ShutdownWaitMs);
                            }

                            audio.Run("--power=0", ShutdownWaitMs);
                        }
                    }
                    finally
                    {
                        audio.EffectsEnabled = wasBypassed;
                    }
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
