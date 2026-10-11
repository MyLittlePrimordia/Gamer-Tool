using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace GamerTool;

public partial class MainWindow
{
    /// <summary>
    /// How often the schedule is checked.
    /// <para>
    /// Fifteen seconds, which is the gap between the room going dark and the
    /// filter coming on. Longer than this is noticeable, and shorter buys
    /// nothing: a tick that changes nothing costs one clock read and two
    /// comparisons, so the cost of being early is not the reason to be careful.
    /// </para>
    /// <para>
    /// A one shot timer aimed at the next boundary would be tidier on paper and
    /// is a worse idea in practice. The boundary moves whenever the user changes
    /// the two times, whenever the machine is asleep across it, and whenever the
    /// clock is adjusted, so every one of those is a place the aimed timer has
    /// to be re-aimed or it is simply wrong until the next app launch.
    /// </para>
    /// </summary>
    private static readonly TimeSpan NightTickInterval = TimeSpan.FromSeconds(15);

    private DispatcherTimer? _nightTimer;

    /// <summary>
    /// Whether the schedule is currently forcing the filter on.
    /// <para>
    /// The user's own level in <see cref="AppSettings.BlueLightFilter"/> is never
    /// written to while this is set, and that is the whole design. The dropdown
    /// on the Display tab and the light on the screen are two different things,
    /// and a schedule that saved over the setting it was applying would leave
    /// EXTRA WARM as the remembered choice after one night, permanently.
    /// </para>
    /// <para>
    /// Cleared by a manual change to the dropdown as well as by the schedule
    /// ending, so a user who picks a level at ten at night gets that level for
    /// the rest of the night. It comes back at the next boundary crossing, which
    /// is the one point where "you asked for something else" is unambiguous.
    /// </para>
    /// </summary>
    private bool _nightApplied;

    /// <summary>
    /// The schedule's answer from the last tick, kept so a change in the
    /// schedule's own state can be told apart from a change the user made.
    /// <para>
    /// The suppression below is meant to last until the schedule's window
    /// changes state, and "the window changed state" is a comparison that needs
    /// the previous answer to make. Without this, a user who picked a level at
    /// ten at night would be back under the schedule within a quarter of a
    /// minute, which is the same as not being allowed to pick a level at all.
    /// </para>
    /// </summary>
    private bool _nightWasWanted;

    /// <summary>
    /// A level picked by hand while the schedule was running, which stands until
    /// the schedule's window next changes state.
    /// <para>
    /// The alternative was leaving the schedule on top of a deliberate choice,
    /// on the grounds that the schedule is what the user asked for in the
    /// first place. In practice a filter that cannot be turned down for the
    /// evening is a filter people stop switching on.
    /// </para>
    /// </summary>
    private bool _nightSuppressed;

    /// <summary>
    /// Whether the night filter has to be put back after a stand-down.
    /// <para>
    /// Pure, so the three-way answer can be tested without standing up a window,
    /// a display and a timer. The question is genuinely three-way rather than a
    /// pair of checks, because the panic key is the case that must differ: every
    /// other stand-down is the app tidying up and the filter is still wanted,
    /// while the panic key is the user saying the screen is wrong right now.
    /// </para>
    /// </summary>
    /// <param name="nightApplied">Whether the schedule believes the filter is on.</param>
    /// <param name="suppressed">Whether the user has overridden it for this window.</param>
    /// <param name="emergency">Whether this stand-down came from the panic key.</param>
    internal static bool ShouldReassertNightAfterStandDown(
        bool nightApplied, bool suppressed, bool emergency) =>
        nightApplied && !suppressed && !emergency;

    /// <summary>
    /// Stands the night filter down for good, until the schedule's window next
    /// changes state.
    /// <para>
    /// Only the panic key uses this. Every other stand-down path leaves the filter
    /// alone, because standing the screen down to take a game preset off is the
    /// app tidying up after itself, and the evening filter is still what the user
    /// asked for.
    /// </para>
    /// </summary>
    private void SuppressNightUntilNextWindow()
    {
        if (!_nightApplied)
        {
            _nightSuppressed = true;
            return;
        }

        // EndNightFilter first, so _nightApplied and _nightSuppressed agree. Setting
        // the flag and letting the next tick notice would leave a window where the
        // flag says the filter is off while the screen is still warm, and the tick's
        // equality check would then do nothing at all.
        EndNightFilter();
        _nightSuppressed = true;
    }

    /// <summary>
    /// Whether the app already had a gamma ramp of its own on screen when the
    /// schedule put the filter on.
    /// <para>
    /// Decides what "take it back off" means at the end of the night. If there
    /// was a tune loaded, the schedule trimmed it and the answer is to push the
    /// same tune again without the trim. If there was not, the only ramp on the
    /// screen is the one the schedule created, and the honest answer is to put
    /// the monitor's own ramp back rather than leave a flat one there being
    /// defended by the gamma lock for the rest of the day.
    /// </para>
    /// </summary>
    private bool _nightRampWasOurs;

    /// <summary>
    /// The blue light level actually in force, which is the user's own choice
    /// except while the schedule is running.
    /// <para>
    /// Read by every display apply rather than only by the schedule's own. That
    /// is what makes the two halves of the app need no coordination: because the
    /// blue light filter is a trim folded in on the way out, a game that loads a
    /// slot at nine in the evening gets the warm trim for free, without the
    /// schedule knowing a game ever started and without the slot system knowing a
    /// schedule exists. The schedule sets the number; the apply path is where it
    /// is used.
    /// </para>
    /// </summary>
    private int ActiveBlueLightLevel =>
        NightSchedule.EffectiveLevel(_nightApplied, _settings.BlueLightFilter);

    /// <summary>
    /// How far into its ramp the night filter currently is, 0 to 1.
    /// <para>
    /// 1 whenever the filter is not running, so this is only ever consulted for a
    /// filter the schedule is holding on. That is what keeps the whole fade
    /// contained inside the night schedule: outside its window nothing here changes,
    /// and the trim is applied at full strength exactly as it always was.
    /// </para>
    /// </summary>
    private double ActiveBlueLightStrength =>
        _nightApplied ? _nightStrength : 1.0;

    /// <summary>
    /// Where the fade has got to, as a fraction of full strength.
    /// <para>
    /// Kept rather than recomputed on demand because the quiet push below needs to
    /// know whether the tint has moved enough to be worth another write to the
    /// monitor. A ramp over thirty minutes sampled every fifteen seconds produces a
    /// change of about one percent a tick, and writing the ramp that often is
    /// pointless I/O against hardware that cannot show the difference.
    /// </para>
    /// </summary>
    private double _nightStrength;

    /// <summary>
    /// Fills the schedule row from the profile.
    /// <para>
    /// Called from <see cref="SyncControlsFromSettings"/> like every other row,
    /// so a restored profile cannot leave the switches showing what the old one
    /// said. The two pickers are set with <c>_updating</c> up, because writing to
    /// a selection is the one thing that makes a control raise the event the
    /// handler is there to catch.
    /// </para>
    /// </summary>
    private void SyncNightScheduleControls()
    {
        _updating = true;
        try
        {
            // A stored time the picker has no entry for is snapped on the way in,
            // so a value a hand-edited profile can hold cannot leave a box
            // showing nothing. Written back as well, so the profile heals itself
            // rather than carrying a value no control can display. The window
            // that runs snaps the same way, so this is belt and braces - but it
            // is the picker that has to be able to show the state it is in.
            _settings.NightStartMinutes = NightSchedule.SnapToStep(_settings.NightStartMinutes);
            _settings.NightEndMinutes = NightSchedule.SnapToStep(_settings.NightEndMinutes);

            NightStartBox.SelectedItem = NightSchedule.FormatMinutes(_settings.NightStartMinutes);
            NightEndBox.SelectedItem = NightSchedule.FormatMinutes(_settings.NightEndMinutes);
            NightBox.IsChecked = _settings.NightBlueLight;
        }
        finally
        {
            _updating = false;
        }

        UpdateNightRowEnabled();
    }

    /// <summary>
    /// Greys the two pickers when the schedule is off.
    /// <para>
    /// The hours are still on show and still say what they are, because hiding
    /// them would make the row change width and the two controls jump about
    /// every time the switch is thrown. A disabled control reading as a value
    /// that is not being used is the honest state, and the one the rest of the
    /// settings list already uses for everything it cannot act on right now.
    /// </para>
    /// </summary>
    private void UpdateNightRowEnabled()
    {
        bool on = _settings.NightBlueLight;
        NightStartBox.IsEnabled = on;
        NightEndBox.IsEnabled = on;
    }

    private void OnNightScheduleChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        _settings.NightBlueLight = NightBox.IsChecked == true;
        UpdateNightRowEnabled();
        Commit();

        // Asked now rather than left to the next tick, so throwing the switch on
        // at 19:58 warms the screen at 19:58 and not up to fifteen seconds later.
        EvaluateNightSchedule();
    }

    private void OnNightTimeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        if (sender is not ComboBox box || box.SelectedItem is not string label)
        {
            return;
        }

        // The picker only ever offers values that parse, so a failure here is a
        // corrupt or foreign profile rather than a person being imprecise. The
        // stored value is left alone in that case, because replacing it with
        // midnight on the strength of a bad read would schedule the filter for
        // the wrong twelve hours.
        if (!NightSchedule.TryParseMinutes(label, out int minutes))
        {
            TraceLog.Write("NIGHT", new FormatException("Unreadable schedule time: " + label));
            return;
        }

        if (box == NightStartBox)
        {
            _settings.NightStartMinutes = minutes;
        }
        else
        {
            _settings.NightEndMinutes = minutes;
        }

        Commit();
        EvaluateNightSchedule();
    }

    /// <summary>
    /// Brings the screen in line with what the schedule says the time is.
    /// <para>
    /// Level triggered rather than edge triggered, on purpose. A machine that
    /// is asleep from eleven to six never ticks across midnight, so an edge
    /// triggered check would still believe what it believed at eleven and would
    /// do nothing at six. Asking every tick what should be on now, and comparing
    /// that to what actually is, is what makes waking up work without a sleep
    /// or resume handler.
    /// </para>
    /// <para>
    /// Reached from the timer, from the switch and from the pickers, and safe to
    /// call in any order: it only acts when the answer differs from the current
    /// state.
    /// </para>
    /// </summary>
    private void EvaluateNightSchedule()
    {
        if (_quitting)
        {
            return;
        }

        bool wanted = NightSchedule.IsActive(
            _settings.NightBlueLight,
            _settings.NightStartMinutes,
            _settings.NightEndMinutes);

        // A level picked by hand stands until the schedule's own window changes
        // state, so the override is dropped here rather than in the handler that
        // set it. That keeps the lifetime in one place: whatever crosses the
        // boundary, in either direction, is the moment the user's choice stops
        // applying.
        if (wanted != _nightWasWanted)
        {
            _nightWasWanted = wanted;
            _nightSuppressed = false;
        }

        bool shouldBeOn = wanted && !_nightSuppressed;

        // Where the fade has got to right now. Zero when suppressed, which is what
        // makes a manual override an instant turn-off rather than a fade back down:
        // somebody who has just picked a level by hand wants it now.
        double strength = _nightSuppressed
            ? 0.0
            : NightFade.Strength(
                _settings.NightBlueLight,
                _settings.NightStartMinutes,
                _settings.NightEndMinutes,
                DateTime.Now.TimeOfDay,
                _settings.NightFadeMinutes);

        if (shouldBeOn == _nightApplied)
        {
            // Already in the state the schedule wants, but the tint has probably
            // moved. Nudged rather than reapplied, and only once it has moved enough
            // to be worth a write - see PushBlueLightQuiet for why the threshold is
            // what it is.
            if (shouldBeOn && Math.Abs(strength - _nightStrength) >= QuietPushThreshold)
            {
                _nightStrength = strength;
                PushBlueLightQuiet();
            }

            return;
        }

        if (shouldBeOn)
        {
            _nightStrength = strength;
            StartNightFilter();
        }
        else
        {
            EndNightFilter();
        }
    }

    /// <summary>
    /// How far the fade has to move before another ramp is worth writing.
    /// <para>
    /// Two percent, which on the extra warm trim is about a single step of blue
    /// channel - below what the eye picks out on a screen that is mostly dark. Over a
    /// thirty minute fade sampled every fifteen seconds, that is one push roughly
    /// every six ticks rather than every tick.
    /// </para>
    /// <para>
    /// Not free-thresholded, because a change small enough that nobody can see it
    /// still has to be decided against. This is the number.
    /// </para>
    /// </summary>
    private const double QuietPushThreshold = 0.02;

    private void StartNightFilter()
    {
        _nightRampWasOurs = _display.IsEnabled;
        _nightApplied = true;

// _nightStrength is not forced to full here, and used to be. The comment
        // that stood here said full strength was for "a profile with no fade
        // set, and for the first push of a window whose fade has already
        // elapsed" - but NightFade.Strength already returns exactly 1.0 in
        // both of those cases, because a zero fade divides to infinity and a
        // machine waking past the ramp is past the ramp. So the caller had
        // already worked out the right value, set it on the line above, and
        // this overwrote it with 1.0 regardless.
        //
        // What that produced on any profile with a fade: a full-strength EXTRA
        // WARM push, then fifteen seconds later a quiet push that found a real
        // difference against that 1.0 and dragged the ramp back to nearly
        // nothing, and only then the fade climbing as designed. The fade ran,
        // but behind a flash and a collapse.
        PushBlueLight();
        Flash("Night blue light on");

        TraceLog.Write("NIGHT on "
            + NightSchedule.FormatMinutes(_settings.NightStartMinutes)
            + " to " + NightSchedule.FormatMinutes(_settings.NightEndMinutes));
    }

    /// <summary>
    /// Stands the schedule down because the user picked a level by hand.
    /// <para>
    /// The level goes out to the screen immediately rather than waiting for the
    /// next apply, which is the one place the blue light dropdown acts on its
    /// own instead of previewing. That is not a special case added for the
    /// schedule: the screen at this moment is running the schedule's trim
    /// rather than the one in the dropdown, so leaving it would mean the two
    /// disagreed. Outside the schedule the dropdown previews as it always has.
    /// </para>
    /// <para>
    /// A ramp of ours is now on the screen whatever the state was a moment ago,
    /// because the level has just been pushed to it.
    /// </para>
    /// </summary>
    private void StandNightFilterDown()
    {
        _nightApplied = false;
        _nightRampWasOurs = true;
        PushBlueLight();
    }

    private void EndNightFilter()
    {
        _nightApplied = false;

        // Cleared rather than left at whatever the ramp had reached, so a window that
        // opens again starts by asking the fade where it is rather than comparing the
        // new ramp against the last one from hours ago.
        _nightStrength = 0.0;

        // The user's own level is already sitting in the profile untouched, so
        // there is nothing to put back there. This is only about the screen.
        //
        // Both branches check that there is still a ramp to act on. Something in
        // between can take it away - a game that exits and auto-reverts, or the
        // user hitting reset - and then there is nothing of ours left on the
        // monitor to put back or take off, and doing it anyway would write a
        // flat ramp over a screen the user had already put right themselves.
        if (!_display.IsEnabled)
        {
            return;
        }

        if (_nightRampWasOurs)
        {
            PushBlueLight();
        }
        else
        {
            // The schedule's filter was the only thing on the screen, so the
            // monitor's own ramp goes back rather than a flat one being left
            // there under the gamma lock until the app is closed.
            //
            // Checked, because a ramp that cannot be taken off is still out there:
            // forgetting which display it was put on would leave the exit path
            // with nothing to look for, and the label would claim the screen is
            // back to normal while it is not.
            if (_display.Reset())
            {
                _appliedMonitor = string.Empty;
            }
            else
            {
                TraceLog.Write("NIGHT the screen would not give the original ramp back");
            }
        }

        UpdateLiveLabels();
        Flash("Night blue light off");

        TraceLog.Write("NIGHT off");
    }

    /// <summary>
    /// Pushes the live blue light level to the screen, and to the dropdown so
    /// the two cannot disagree.
    /// <para>
    /// Goes back through <see cref="ApplyDisplay"/> rather than poking the
    /// display service, so the same code that applies a tune the user chose also
    /// applies the one the schedule chose, on the same monitor, with the same
    /// labels and the same save. The one thing that is not repeated is the
    /// announce, because a screen that warmed itself should not also shout
    /// "applied" over the top of a game.
    /// </para>
    /// <para>
    /// Not used for the intermediate steps of a fade. This path saves the profile,
    /// repaints the labels and announces the apply, which is right when the filter
    /// switches and wrong every half minute for half an hour - a toast repeating is
    /// worse than no fade at all. Those go through <see cref="PushBlueLightQuiet"/>
    /// instead.
    /// </para>
    /// </summary>
    private void PushBlueLight()
    {
        int level = ActiveBlueLightLevel;

        _updating = true;
        try
        {
            BlueLightBox.SelectedItem = DisplayPreset.BlueLightNames[
                Math.Clamp(level, 0, DisplayPreset.BlueLightNames.Length - 1)];
        }
        finally
        {
            _updating = false;
        }

        DisplayPreset? active = FindDisplay(_activeDisplayId);
        if (active is null)
        {
            // The preset the schedule is re-pushing has been deleted since it was
            // loaded - a custom screen the user removed while the filter was on.
            // Nothing was written, so the schedule must not go on believing the
            // screen is warm: _nightApplied is already true by this point, and the
            // end-of-window branch would then take the "our ramp is still out
            // there" path and re-push nothing. Saying so is what lets the next
            // tick try again against whatever is loaded now.
            TraceLog.Write("NIGHT the active screen preset is gone, so nothing was pushed");
            return;
        }

        if (!ApplyDisplay(active.Copy(), _appliedMonitor, false))
        {
            // Blocked by the display. The flag is cleared for the same reason: the
            // schedule's own record of what is on the screen has to match reality,
            // or the end-of-window branch acts on a ramp that was never written.
            _nightApplied = false;
            TraceLog.Write("NIGHT the screen refused the filter, so the schedule will try again");
        }
    }

    /// <summary>
    /// Moves the tint along without announcing it, saving it or repainting a label.
    /// </summary>
    /// <remarks>
    /// This is the path every intermediate step of a fade takes, and it deliberately
    /// does none of the three things <see cref="PushBlueLight"/> does.
    /// <para>
    /// No toast, because over a thirty minute fade that is one every few seconds and
    /// a filter that keeps announcing itself is worse than one that does not fade. No
    /// profile write, because the strength is derived from the clock rather than
    /// stored - saving it would write the same file twenty times an evening to record
    /// something that will be different next time. No label change, because the
    /// dropdown shows the level and the level is not what is moving.
    /// </para>
    /// <para>
    /// <c>SetWorking</c> before <c>Push</c> because the gamma lock re-pushes whatever
    /// is in Working, and pushing without setting it would write this step and then
    /// let the lock put the previous step straight back on the next tick.
    /// </para>
    /// <para>
    /// Refuses when there is nothing to push onto, for the same reason
    /// <see cref="PushBlueLight"/> does: writing over a screen the user has already
    /// put right themselves is worse than not fading.
    /// </para>
    /// </remarks>
    private void PushBlueLightQuiet()
    {
        if (!_display.IsEnabled)
        {
            return;
        }

        DisplayPreset? active = FindDisplay(_activeDisplayId);
        if (active is null)
        {
            TraceLog.Write("NIGHT the active screen preset is gone, so the fade stopped");
            return;
        }

        DisplayPreset effective = DisplayPreset.WithBlueLight(
            active.Copy(), ActiveBlueLightLevel, ActiveBlueLightStrength);

        _display.SetWorking(effective);
        if (!_display.Push())
        {
            TraceLog.Write("NIGHT a fade step was refused by the display");
        }
    }

    private void StartNightScheduleTimer()
    {
        if (_nightTimer is not null)
        {
            return;
        }

        _nightTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = NightTickInterval
        };

        _nightTimer.Tick += OnNightTick;
        _nightTimer.Start();

        // The first answer, once the monitors are known. A profile that was
        // switched on at eight in the evening has to be warm when the app opens
        // at half past eight, and the first tick is a quarter of a minute away.
        EvaluateNightSchedule();
    }

    private void OnNightTick(object? sender, EventArgs e) => EvaluateNightSchedule();

    private void StopNightScheduleTimer()
    {
        if (_nightTimer is null)
        {
            return;
        }

        _nightTimer.Stop();
        _nightTimer.Tick -= OnNightTick;
        _nightTimer = null;
    }
}
