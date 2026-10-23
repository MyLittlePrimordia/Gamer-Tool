using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
namespace GamerTool;

// Following a game once it is running: the foreground-change handler, the wildcard
// that applies to a game nobody bound, the focus pause and the grace timer behind it,
// and the launch and exit events that take a slot back off again.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    private void OnForegroundChanged(WatchedWindow window)
    {
        if (!_settings.AutoSwitch)
        {
            return;
        }

        // Back on something real, so any pending pause is off. Before the match,
        // because the returning window may be a different program entirely - a
        // browser rather than the game - and a pause armed by the last alt-tab
        // must not survive the user coming back to read a message.
        CancelFocusPause();

        // A specific match always wins over the wildcard. The wildcard is a
        // fallback for a game nobody remembered to bind, not a competitor - if it
        // were checked first, every bound game would be at the mercy of list order.
        HotkeySlot? slot = SlotService.MatchForeground(_settings.Slots, window.ExePath, window.ProcessName);

        bool wildcard = false;
        if (slot is null)
        {
            slot = SlotService.MatchWildcard(_settings.Slots);
            wildcard = slot is not null;

            // The fullscreen test, here rather than in the matcher because it needs
            // the window handle and the matcher is handed strings. Without it the
            // wildcard applies to every window the user alt-tabs to, which is
            // worse than not having the feature: it would take over the picture of
            // a browser and every document they open.
            if (slot is not null && !IsFullscreen(window))
            {
                TraceLog.Write("AUTO WILDCARD skip " + window.ProcessName + " (not fullscreen)");
                RevertWildcardOnFocusLoss(window);
                return;
            }
        }

        if (slot is null || !ShouldAutoApply(slot, window.ProcessName))
        {
            return;
        }

        TraceLog.Write((wildcard ? "AUTO WILDCARD " : "AUTO FOCUS ") + slot.Name + " <- " + window.ExePath);

        if (wildcard)
        {
            // Remembered so the revert knows what to undo. The wildcard matched a
            // process rather than a slot's own game, and OnTargetExited looks the
            // slot up by process name - which finds nothing here, because this
            // process is not bound to anything. Without this the profile would be
            // left boosted for whatever the user ran next.
            _autoWildcardProcess = window.ProcessName;

            // And registered with the watcher, which is the other half of that.
            // OnTargetExited only ever hears about a process the scan considers
            // alive-and-then-gone, and a wildcard contributes nothing to the watch
            // list, so without this its exit was never noticed at all. The revert
            // was fully written and simply never ran.
            //
            // The refusal is checked rather than dropped. An empty return means a
            // different process is still being watched, so this game is applied but
            // unmonitored and its exit will never revert anything - which used to
            // be recorded nowhere at all.
            if (_watcher.WatchUnbound(window.ProcessName).Length == 0)
            {
                TraceLog.Write("WATCH refused " + window.ProcessName + " <- already watching a different process");
            }
        }

        // The screen is coming back rather than being applied fresh. Without this
        // the sound would be pushed again on every return from a pause, and a return
        // is not a rare event - it is every time the user alt-tabs back to check
        // something.
        bool resuming = string.Equals(_pausedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase);

        if (resuming)
        {
            TraceLog.Write("AUTO FOCUS resume " + slot.Name);
        }

        PlaySlot(slot, true, screenOnly: resuming);

        if (resuming)
        {
            ClearFocusPause();
        }
    }

    /// <summary>
    /// Arms the screen pause, if a pause is warranted at all.
    /// </summary>
    /// <remarks>
    /// Called from two places: a real window taking the foreground, and the watcher
    /// saying the shell has it. The two differ because the first can also be
    /// <em>back</em> to the game, and returning is not leaving - which is why the
    /// match is on the foreground process name rather than merely "somebody else is
    /// in front".
    /// </remarks>
    private void ConsiderFocusPause(string foregroundProcess)
    {
        if (!_settings.AutoSwitch || !_settings.AutoPauseOnFocusLoss)
        {
            return;
        }

        if (!ShouldPauseForFocusLoss(
                enabled: true,
                autoSwitch: true,
                autoSlotId: _autoSlotId,
                wildcardProcess: _autoWildcardProcess,
                autoProcess: _autoProcess,
                foregroundProcess: foregroundProcess))
        {
            CancelFocusPause();
            return;
        }

        if (_focusPause is not null)
        {
            // Already armed. Not restarted: a timer that restarted on every tick
            // would never fire while the user sat still, which is the one case the
            // pause is for.
            return;
        }

        _focusPause = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(AppSettings.FocusPauseGraceSeconds)
        };

        _focusPause.Tick += OnFocusPauseTick;
        _focusPause.Start();
    }

    /// <summary>
    /// Whether the screen should be paused because something other than the game is
    /// in front.
    /// </summary>
    /// <remarks>
    /// Pure, so the whole decision can be tested without a desktop, a display or a
    /// timer. Three of the six conditions are exclusions that each prevent a
    /// specific wrong answer:
    /// <para>
    /// No auto-slot means nothing of ours is loaded, so there is nothing to pause.
    /// </para>
    /// <para>
    /// A wildcard process means the existing wildcard revert owns this case. It
    /// already reverts on focus loss, on its own terms, and arming a second
    /// mechanism alongside it would have two answers to one question.
    /// </para>
    /// <para>
    /// The same process name means the user is still in the game - another window
    /// of it, or a second instance. Pausing then would suspend the boost for
    /// looking at the game's own launcher.
    /// </para>
    /// </remarks>
    internal static bool ShouldPauseForFocusLoss(
        bool enabled,
        bool autoSwitch,
        string autoSlotId,
        string wildcardProcess,
        string autoProcess,
        string foregroundProcess)
    {
        if (!enabled || !autoSwitch)
        {
            return false;
        }

        if (autoSlotId.Length == 0)
        {
            return false;
        }

        if (wildcardProcess.Length > 0)
        {
            return false;
        }

        // Empty is "nothing real in front", not "the user went somewhere else". Read
        // returns null in several cases that are not the desktop - a process that
        // would not hand over its image path, an exception in the read itself - and
        // pausing on any of those would suspend a boost for no reason at all.
        if (foregroundProcess.Length == 0)
        {
            return false;
        }

        return !string.Equals(foregroundProcess, autoProcess, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The slot whose screen was paused, so it can be told apart from the slot the
    /// app is currently applying.
    /// </summary>
    /// <remarks>
    /// Separate from <c>_autoSlotId</c> rather than reusing it, because the pause
    /// stands the screen down and <c>StandDownScreen</c> clears <c>_autoSlotId</c> on
    /// its way through. Storing it here is what lets the exit path still find the
    /// slot after a pause, which is the case that would otherwise leave the sound
    /// boosted once the game closed.
    /// </remarks>
    private string _pausedSlotId = string.Empty;

    /// <summary>Which process the paused slot was loaded for.</summary>
    private string _pausedProcess = string.Empty;

    /// <summary>One shot, armed by leaving and fired by still being away.</summary>
    private DispatcherTimer? _focusPause;

    /// <summary>
    /// The grace period ran out and the user is still not in the game.
    /// </summary>
    private void OnFocusPauseTick(object? sender, EventArgs e)
    {
        CancelFocusPause();

        if (_quitting || _autoSlotId.Length == 0)
        {
            // Quitting, or already stood down by something else in the meantime.
            // _autoSlotId is the authority rather than the armed slot id, because
            // it is the field the app itself set and the question is whether
            // anything of ours is still loaded.
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, _pausedSlotId, StringComparison.OrdinalIgnoreCase));

        if (slot is null)
        {
            // Deleted or disabled while paused. Nothing to pause and nothing to say.
            ClearFocusPause();
            return;
        }

        // Taken into locals first. StandDownScreen empties _autoSlotId AND
        // _autoProcess on its way through, so reading them after the call to
        // restore them would store the empty strings - and the exit path would
        // then fail to match the paused game's process, which is the one thing
        // this record exists for.
        string slotId = _autoSlotId;
        string process = _autoProcess;

        _pausedSlotId = slotId;
        _pausedProcess = process;

        // HDR deliberately stays on while the user is away. Only the gamma ramp
        // stands down; a mode flip here would blink the screen on every alt-tab.
        GoScreenStandDownForFocusPause();

        // Restored afterwards, because StandDownScreen also clears a pause that is
        // still armed - correct for every other caller, since a user taking the
        // screen back means the pause is over, but here it would take the record
        // this path depends on.
        _pausedSlotId = slotId;
        _pausedProcess = process;

        TraceLog.Write("AUTO FOCUS paused " + slot.Name + " after "
            + AppSettings.FocusPauseGraceSeconds + "s away");
        Flash(slot.Name + " paused while you are away");
    }

    /// <summary>Disarms a pending pause. Does not un-pause an already paused screen.</summary>
    private void CancelFocusPause()
    {
        if (_focusPause is null)
        {
            return;
        }

        _focusPause.Stop();
        _focusPause.Tick -= OnFocusPauseTick;
        _focusPause = null;
    }

    /// <summary>Forgets the pause entirely, used when the screen is back.</summary>
    private void ClearFocusPause()
    {
        CancelFocusPause();
        _pausedSlotId = string.Empty;
        _pausedProcess = string.Empty;
    }

    /// <summary>
    /// The watcher says the desktop or the shell has the foreground.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OnForegroundChanged"/> because that one is never
    /// called for this at all - the shell processes are filtered out of
    /// <c>Read()</c>, which is why <c>ForegroundLeft</c> exists.
    /// </remarks>
    private void OnForegroundLeft(string shell)
    {
        if (!_settings.AutoSwitch || !_settings.AutoPauseOnFocusLoss)
        {
            return;
        }

        // Nothing of ours loaded, or the wildcard owns this case already.
        if (_autoSlotId.Length == 0 || _autoWildcardProcess.Length > 0)
        {
            return;
        }

        _pausedSlotId = _autoSlotId;
        TraceLog.Write("AUTO FOCUS left to " + shell + ", arming the pause");
        ConsiderFocusPause(shell);
    }

    /// <summary>
    /// Whether the window covers its whole monitor.
    /// <para>
    /// The shape that reliably means a game is running. A maximised browser covers
    /// the work area and leaves the taskbar visible, so comparing against the work
    /// area would match half the desktop; comparing against the monitor rectangle
    /// is what distinguishes the two, and it is the same distinction the gamma lock
    /// has to make before deciding not to fight something on screen.
    /// </para>
    /// <para>
    /// Fails closed. If the window's own rectangle cannot be read - which happens
    /// on a desktop that is being torn down, and for windows owned by another
    /// user's session - the answer is no, so the wildcard stands down rather than
    /// guessing.
    /// </para>
    /// </summary>
    private static bool IsFullscreen(WatchedWindow window)
    {
        return window.IsFullscreen;
    }

    /// <summary>
    /// The wildcard's game is still running but the user has left it, so the preset
    /// goes back to neutral.
    /// <para>
    /// The other half of the fullscreen rule, and it was missing. The rule answers
    /// "when should this apply", and until now nothing answered "when should it
    /// stop" - so alt-tabbing to a browser left the game profile on a browser, and
    /// the wildcard would not re-apply either because the browser is not fullscreen.
    /// The screen stayed wrong until the user pressed something.
    /// </para>
    /// <para>
    /// Gated on this being the wildcard's own application. Reverting because the
    /// user alt-tabbed into some unrelated fullscreen window would undo a preset
    /// they had chosen deliberately, and a game that owns a bound slot has its own
    /// exit handling which does not work this way.
    /// </para>
    /// <para>
    /// Deliberately does not release the watch. The game is still running, so
    /// quitting it should still revert - and if the user alt-tabs back in, the
    /// wildcard applies again on the normal path.
    /// </para>
    /// </summary>
    private void RevertWildcardOnFocusLoss(WatchedWindow window)
    {
        // Whether the wildcard should stand down at all, as one named question. The
        // second half is the one that is easy to get wrong: the game having another
        // window in front - its own launcher, a second instance of itself - is not a
        // focus loss, and standing down for it would suspend the boost for looking
        // at the game.
        if (!AutoApplyDecision.WildcardShouldStandDown(_autoWildcardProcess, window.ProcessName))
        {
            return;
        }

        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, _autoSlotId, StringComparison.OrdinalIgnoreCase));

        if (slot is null || !slot.IsAnyGameTarget)
        {
            return;
        }

        if (!_settings.AutoRevertOnExit || _quitting)
        {
            return;
        }

        TraceLog.Write("AUTO WILDCARD focus left " + _autoWildcardProcess
            + " <- " + window.ProcessName);

        // The apply debounce is cleared inside GoScreenStandDown, which is where all four
        // stand-down paths now clear it. This used to be a second site, and the two
        // did not agree.

        // A stand-down rather than a hard reset: the game is over, not the screen
        // broken. Same reasoning as the exit path in OnTargetExited - the panel
        // goes back to where the user had it, unless this preset never touched it.
        GoScreenStandDown();

        // And the same pre-game record the exit path spends, for the same reason:
        // a user who had a reading preset on before going fullscreen gets it back
        // rather than a flat screen, and the sound falls through to neutral when
        // there was nothing there.
        RevertToPreGame(slot);
        _ = GoSoundNeutralAsync();

        Flash(slot.Name + " left behind, back to normal");
    }


    private void OnTargetLaunched(WatchedWindow window)
    {
        if (!_settings.AutoSwitch)
        {
            return;
        }

        HotkeySlot? slot = SlotService.MatchProcessName(_settings.Slots, window.ProcessName);
        if (slot is not null && ShouldAutoApply(slot, window.ProcessName))
        {
            TraceLog.Write("AUTO LAUNCH " + slot.Name + " <- " + window.ExePath);
            PlaySlot(slot, true);
        }
    }


    /// <summary>
    /// A game the app loaded a slot for has closed, so the screen and sound go
    /// back to neutral rather than staying boosted for whatever comes next.
    /// <para>
    /// Only ever undoes a slot this app applied by itself. The guard is the id
    /// left behind by the auto-apply that matched, so if the user has since
    /// pressed Reset, hit the slot's own key, or loaded a different tune, there is
    /// nothing to undo and the exit is ignored. Without that, quitting a game
    /// would stomp a preset the user had chosen on purpose.
    /// </para>
    /// </summary>
    private void OnTargetExited(string processName)
    {
        // The paused case is asked first, in this method, so that the order is
        // visible where it matters rather than implied by a helper's parameter list.
        // It has to come before the revert-on-exit guard below: a focus pause stands
        // the screen down and clears the auto slot on its way through, so by the time
        // a paused game exits there is no auto slot left to match, and with the guard
        // first the exit would be ignored - leaving the sound boosted for whatever the
        // user runs next, which is the one thing this path exists to prevent.
        //
        // Sound only, because the screen is already neutral: pushing it again would be
        // doing work that is not needed.
        bool startedPaused = _pausedSlotId.Length > 0
            && string.Equals(_pausedProcess, processName, StringComparison.OrdinalIgnoreCase);

        if (startedPaused)
        {
            TraceLog.Write("AUTO EXIT while paused " + processName);

            // Cleared either way. The pause is over whatever the setting says, and
            // leaving the record standing would have the next exit match it again.
            ClearFocusPause();

            if (_settings.AutoRevertOnExit && !_quitting)
            {
                _ = GoSoundNeutralAsync();
                Flash("Game closed, back to normal");
            }

            return;
        }

        if (!_settings.AutoRevertOnExit || _quitting)
        {
            return;
        }

        // Everything from here is a pure decision, so the remaining three answers and
        // the order they are asked in are asserted by test rather than by a comment.
        HotkeySlot? bound = SlotService.MatchProcessName(_settings.Slots, processName);

        HotkeySlot? autoSlot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, _autoSlotId, StringComparison.OrdinalIgnoreCase));

        RevertDecision decision = AutoApplyDecision.ClassifyExit(
            revertEnabled: true,
            pausedExit: false,
            pausedSlotId: string.Empty,
            exitedProcess: processName,
            wildcardProcess: _autoWildcardProcess,
            autoSlotId: _autoSlotId,
            boundSlotId: bound?.Id ?? string.Empty,
            autoSlotIsWildcard: autoSlot?.IsAnyGameTarget == true);

        if (!decision.ShouldRevert)
        {
            // Said out loud because the common case for a watched process going away
            // is that it was never ours, and "the app changed nothing" is the
            // outcome the whole guard exists to produce.
            if (autoSlot is not null || bound is not null)
            {
                TraceLog.Write("AUTO EXIT SKIP " + processName + " <- not the auto loaded slot");
            }

            return;
        }

        // The slot the decision named, which for the wildcard case is the wildcard
        // rather than anything bound to the process - the wildcard matched something
        // that is bound to nothing, which is why the name lookup cannot find it.
        HotkeySlot? slot = _settings.Slots.FirstOrDefault(s =>
            string.Equals(s.Id, decision.SlotId, StringComparison.OrdinalIgnoreCase));

        if (slot is null)
        {
            // Deleted between the apply and the exit. Nothing to revert and nothing
            // to say; the guard above already decided this exit was ours.
            TraceLog.Write("AUTO EXIT SKIP " + processName + " <- the slot has gone");
            return;
        }

        if (decision.Kind == RevertKind.Wildcard)
        {
            TraceLog.Write("AUTO WILDCARD EXIT " + slot.Name + " <- " + processName);
        }

        TraceLog.Write("AUTO EXIT " + slot.Name + " <- " + processName);

        // Released explicitly rather than left to the watcher's own exit handling,
        // which already removed it before raising this. Named here so the two
        // paths - the wildcard's exit and the wildcard's focus loss - release the
        // same thing, and so a second exit for the same process cannot be raised
        // by a name that is no longer being watched.
        _watcher.ForgetUnbound(processName);

        // GoScreenStandDown stands the auto-apply guard down as it goes, so a second
        // exit for the same slot cannot fire a second revert. And the panel is put
        // back rather than left at the game's brightness, because the desktop the
        // user is returning to is theirs and should be at the brightness they set
        // - but only if this preset was what moved it.
        GoScreenStandDown();

        // After the guard is down, not before it. GoScreenStandDown is what clears
        // _autoSlotId, and restoring first would leave the guard standing with
        // nothing behind it - so the next exit for any process would try to revert
        // a second time.
        //
        // The screen the user had before the game, if there was one, and otherwise
        // nothing: the neutral reset below is the fallback and is what every user
        // with nothing loaded gets, exactly as before.
        RevertToPreGame(slot);

        // Off the dispatcher. This is three engine calls that each wait on a child
        // process, and it runs at the moment a game exits, which is the worst
        // possible time to stop the window answering. GoSoundNeutralAsync puts its
        // own window work back on the dispatcher before it returns.
        //
        // Runs after the restore as well as instead of it: the restore puts the
        // user's own curve back on the engine, and the reset is what stands the
        // engine down for a user who had nothing loaded.
        _ = GoSoundNeutralAsync();

        Flash(slot.Name + " closed, back to normal");
    }


    private bool ShouldAutoApply(HotkeySlot slot, string processName)
    {
        // Whether this exact pair is already loaded, as a named question rather than
        // as an inline comparison of two fields. Both halves are needed and the
        // reason is worth keeping next to the answer: the slot id alone would refuse
        // a second game started under a different process, and the process alone
        // would refuse a different slot loaded for the same program.
        if (AutoApplyDecision.AlreadyApplied(_autoSlotId, _autoProcess, slot.Id, processName))
        {
            return false;
        }

        if (AutoApplyGate.IsDebounced(_autoStamp, DateTime.UtcNow))
        {
            return false;
        }

        // Before the guard below sets it, and only when nothing of ours is already
        // loaded. The emptiness of _autoSlotId is the test for "the app is not
        // currently in charge": reading it after the assignment would record the
        // game's own preset as the thing to put back on exit.
        if (_autoSlotId.Length == 0)
        {
            CapturePreGame();
        }

        _autoStamp = DateTime.UtcNow;
        _autoSlotId = slot.Id;
        _autoProcess = processName ?? string.Empty;

        // A wildcard application is remembered against its process only. A slot
        // applied for a bound game is undone through OnTargetExited's own lookup,
        // which finds it by name, and a wildcard's name matches nothing - so the
        // two cannot share a field without one of them reading the other's answer.
        _autoWildcardProcess = slot.IsAnyGameTarget ? _autoProcess : string.Empty;
        return true;
    }
}
