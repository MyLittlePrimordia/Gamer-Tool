using System;
using System.Collections.Generic;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// Tracks how many rounds in a row every connected display has refused to be
/// driven, and decides when the feature should stop offering itself.
/// <para>
/// Pulled out of <see cref="BacklightService"/> so the policy can be tested
/// without a monitor, which is the only way the interesting cases exist at all:
/// three displays where two fail, a bus that was merely busy, a machine that
/// recovers on the second try.
/// </para>
/// <para>
/// The rule it encodes is that a settings row which controls nothing is noise.
/// The user is not failing to discover a useful control, they are looking at a
/// dead switch, and the switch is dead because their hardware cannot do the thing
/// rather than because the app is broken.
/// </para>
/// </summary>
public sealed class BacklightAvailability
{
    /// <summary>
    /// Consecutive all-displays-failed rounds before the feature turns itself off.
    /// <para>
    /// Two, and not one, because one round of failure is ordinary: a monitor
    /// waking from sleep, a bus another thread was using, a display arriving
    /// mid-resume. Every one of those is a real event that this app sees, and
    /// retiring the feature on a single round would hide it from people whose
    /// hardware works perfectly well most of the time.
    /// </para>
    /// <para>
    /// Two, and only two, because each of them costs the user a restart. A round
    /// only happens on a launch, so this number is the number of times somebody
    /// has to close and reopen the app before the switch moves itself. The probe
    /// that fires when the switch is turned on deliberately does not count, for
    /// the reasons on <see cref="RecordWithoutCounting"/>, so this is the whole of
    /// the grace rather than half of it spent instantly.
    /// </para>
    /// </summary>
    public const int RoundsBeforeRetiring = 2;

    private int _consecutiveRounds;

    /// <summary>Failed rounds in a row. Zero means the last probe reached something.</summary>
    public int ConsecutiveRounds => _consecutiveRounds;

    /// <summary>
    /// True once the app has turned the feature off by itself, so the settings tab
    /// can say so rather than the switch simply being off.
    /// <para>
    /// Never silently. A switch that changes itself and gives no explanation is
    /// indistinguishable from a bug, and the user has no way to tell the
    /// difference between "the app decided this" and "something else turned it
    /// off".
    /// </para>
    /// <para>
    /// Seeded from settings at construction and written back to them, because the
    /// note has to survive a restart. A switch that turned itself off yesterday
    /// needs explaining today, before anything has been probed again.
    /// </para>
    /// </summary>
    public bool Retired { get; set; }

    /// <summary>
    /// How many displays were in the round that counted, for the log.
    /// <para>
    /// Deliberately not persisted. It is a count of what one particular probe
    /// happened to see, and carrying it to the next launch produces a sentence
    /// about a machine as it was rather than as it is: on a fresh start it reads
    /// zero, so the dialog would say "none of your 0 displays could be reached".
    /// The next probe fills it in, and until then the copy avoids naming a number.
    /// </para>
    /// </summary>
    public int LastRoundDisplays { get; set; }

    /// <summary>True the first time a round pushes the count over the threshold.</summary>
    public bool JustRetired { get; private set; }

    /// <summary>
    /// Records one completed probe and says whether the feature should now turn
    /// itself off.
    /// <para>
    /// Only ever called for a machine where the user already opted in. A fresh
    /// install never probes, so there is nothing here to retire, and silently
    /// writing to a setting nobody touched is how an app loses trust in its own
    /// state.
    /// </para>
    /// </summary>
    public bool Record(IReadOnlyList<MonitorProbe> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        LastRoundDisplays = monitors.Count;
        JustRetired = false;

        if (monitors.Count == 0 || monitors.Any(m => m.CanControlBacklight))
        {
            // Something answered. Whatever went wrong before was not about this
            // machine, and keeping the count would retire a feature that works.
            _consecutiveRounds = 0;
            return false;
        }

        // A round where every display was skipped counts as evidence, and that is
        // the one thing this class exists not to do. Busy and TimedOut are the
        // outcomes that say the monitor was never spoken to: the bus was held by
        // another caller, or it did not answer in time. CanControlBacklight is
        // false for both, because it is derived from there being no reading, so
        // they landed here as indistinguishable from a monitor that declined three
        // times - which is the specific mistake BusOutcome.Busy was split out to
        // stop happening. Two quiet rounds then turned the feature off on a machine
        // whose hardware was never asked a question.
        //
        // A round in which nothing was even attempted is not a failed round. It is
        // not a pass either, so the streak is left exactly as it was rather than
        // reset: a real refusal next time still counts as the first of two, and a
        // working display still resets immediately through the branch above.
        if (monitors.All(m => !m.WasAsked))
        {
            return false;
        }

        _consecutiveRounds++;

        if (_consecutiveRounds < RoundsBeforeRetiring)
        {
            return false;
        }

        if (Retired)
        {
            // Already off. Staying off is the point, so this is not re-asserted
            // and does not count as a fresh retirement.
            return false;
        }

        Retired = true;
        JustRetired = true;
        return true;
    }


    /// <summary>
    /// Takes the retirement notice, so it is acted on once.
    /// <para>
    /// Without this the notice is a flag nobody clears. The probe sets it and
    /// stops running - the feature has turned itself off, so there is nothing
    /// left for a probe to record - which means nothing would ever clear it, and
    /// every later read would re-announce a retirement that had already been
    /// dealt with.
    /// </para>
    /// </summary>
    public void AcknowledgeRetirement() => JustRetired = false;


    /// <para>
    /// For the probe that fires the instant the user turns the feature back on.
    /// That round is the app answering its own switch, not a second opinion from
    /// a later session: it runs within a second of the click, over the same
    /// connection, in the same conditions, and on hardware that has just failed
    /// twice it is going to fail a third time for the same reason. Counting it
    /// spent half the grace period before the user had closed the window.
    /// </para>
    /// <para>
    /// The display count is still recorded and the streak still resets if
    /// something answered, so this is not a way to accumulate a free pass: a
    /// display that works is believed immediately, exactly as before.
    /// </para>
    /// </summary>
    public void RecordWithoutCounting(IReadOnlyList<MonitorProbe> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        LastRoundDisplays = monitors.Count;

        if (monitors.Count > 0 && monitors.Any(m => m.CanControlBacklight))
        {
            _consecutiveRounds = 0;
        }
    }

    /// <summary>
    /// Forgets the count, without un-retiring.
    /// <para>
    /// Used by the row switch, so that turning the feature back on gives it two
    /// full rounds of grace rather than immediately retiring again on the next
    /// failure. The retired flag deliberately survives: the user is being given a
    /// second chance, not told the machine is fine.
    /// </para>
    /// </summary>
    public void ResetCount()
    {
        _consecutiveRounds = 0;
        JustRetired = false;
    }

    /// <summary>
    /// Called when the user turns the feature on by hand. This is the one place
    /// that un-retires, because it is the only evidence available that the
    /// situation has changed.
    /// </summary>
    public void Restore()
    {
        ResetCount();
        Retired = false;
    }

    /// <summary>
    /// Carries the count and the retired flag in and out of settings.
    /// <para>
    /// A round spans a launch on purpose. The probe runs once per launch, so a
    /// counter kept only in memory could never reach the threshold and the note
    /// could never appear: two separate occasions, each with the machine freshly
    /// started, is a better test of a feature than two probes in one session,
    /// because a second probe in the same session is likely to hit the same
    /// transient that made the first one fail.
    /// </para>
    /// </summary>
    public void RestoreFrom(int rounds, bool retired)
    {
        _consecutiveRounds = Math.Max(0, rounds);
        Retired = retired;
    }

    /// <summary>What gets written to settings, and what a round needs to carry forward.</summary>
    public (int Rounds, bool Retired) Persistable() => (_consecutiveRounds, Retired);

    /// <summary>
    /// The full explanation, for the dialog the info badge opens.
    /// <para>
    /// States what was measured and stops short of naming a setting to change. It
    /// used to name one, and on real hardware that advice was wrong: it was for a
    /// graphics setting that was already correct, and following it cost several
    /// restarts and changed nothing. The app also cannot tell whether following its
    /// own advice helped, so anything specific would be a guess presented as a
    /// diagnosis.
    /// </para>
    /// <para>
    /// Two sentences, and there used to be a second and third form of this copy.
    /// A one line nudge was printed under the switch in the settings list, and a
    /// longer paragraph before that. Both are gone: the nudge occupied a row of its
    /// own in the middle of a column of switches, and the reason it existed has
    /// been replaced by a badge on the switch itself, which says the same thing in
    /// a shape that costs no vertical space.
    /// </para>
    /// </summary>
    public string Note
    {
        get
        {
            if (!Retired)
            {
                return string.Empty;
            }

            // The count is only named when this session has a probe to describe.
            // After a restart there has not been one yet, and a count of zero is
            // not a thing to put in front of somebody.
            string scope = LastRoundDisplays > 0
                ? "None of your " + CountWord(LastRoundDisplays) + " could be reached over DDC/CI in "
                    + RoundsBeforeReturing() + " tries in a row, "
                : "No display could be reached over DDC/CI in "
                    + RoundsBeforeReturing() + " tries in a row, ";

            return scope
                + "so Gamer Tool turned this off rather than leave a switch that does nothing.\n\n"
                + "This normally means the graphics driver is not providing the link to the"
                + " display, not that the display itself is broken.";
        }
    }

    private static string RoundsBeforeReturing() =>
        RoundsBeforeRetiring.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string CountWord(int displays) => displays.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (displays == 1 ? " display" : " displays");
}
