using System;
using System.Collections.Generic;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// When the app is allowed to turn the feature off by itself.
/// <para>
/// The policy is small and every branch of it has a real event behind it, which
/// is why the interesting cases are all shapes of failure rather than success: a
/// monitor that was asleep, a bus another thread was holding, a display arriving
/// mid-resume, a machine with three screens where only one answers.
/// </para>
/// </summary>
public class BacklightAvailabilityTests
{
    private static MonitorProbe Live() => new()
    {
        DeviceName = @"\\.\DISPLAY1",
        FriendlyName = "Working",
        Outcome = BusOutcome.Ok,
        Brightness = new BrightnessReading { Minimum = 0, Current = 50, Maximum = 100 }
    };

    private static MonitorProbe Dead(string name = @"\\.\DISPLAY2") => new()
    {
        DeviceName = name,
        FriendlyName = "Not working",
        Outcome = BusOutcome.NoDdcPathway
    };

    [Fact]
    public void One_failed_round_does_not_retire_anything()
    {
        // The single most important test here. A display that was asleep, on a
        // busy bus, or arriving mid-resume produces exactly this, and all three
        // happen to hardware that works fine the rest of the time.
        var availability = new BacklightAvailability();

        Assert.False(availability.Record(new List<MonitorProbe> { Dead() }));
        Assert.False(availability.Retired);
        Assert.Equal(1, availability.ConsecutiveRounds);
    }

    [Fact]
    public void Two_failed_rounds_in_a_row_retire_the_feature()
    {
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.True(availability.Record(new List<MonitorProbe> { Dead() }));
        Assert.True(availability.Retired);
        Assert.True(availability.JustRetired);
    }

    [Fact]
    public void A_display_that_answers_resets_the_count()
    {
        // A streak has to be consecutive to mean anything. One success says the
        // hardware is fine and everything before it was a moment.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Live() });

        Assert.Equal(0, availability.ConsecutiveRounds);
        Assert.False(availability.Retired);
    }

    [Fact]
    public void A_success_after_the_first_failure_prevents_retirement_entirely()
    {
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Live() });

        Assert.False(availability.Record(new List<MonitorProbe> { Live() }));
        Assert.False(availability.Retired);
    }

    [Fact]
    public void One_working_display_among_several_keeps_the_feature()
    {
        var availability = new BacklightAvailability();
        List<MonitorProbe> three = new() { Dead(), Dead(@"\\.\DISPLAY3"), Live() };

        availability.Record(three);

        Assert.False(availability.Record(three));
        Assert.False(availability.Retired);
    }

    [Fact]
    public void A_round_with_no_displays_resets_rather_than_counting()
    {
        // Lid closed, or a transient during a resolution change. Counting this
        // would hide the feature on hardware that works.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });

        availability.Record(new List<MonitorProbe>());

        Assert.Equal(0, availability.ConsecutiveRounds);
    }

    [Fact]
    public void A_streak_broken_by_an_empty_round_then_restarted_reaches_the_threshold()
    {
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe>());
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.False(availability.Retired);
        Assert.True(availability.Record(new List<MonitorProbe> { Dead() }));
    }

    [Fact]
    public void Retirement_is_asserted_once_and_not_repeated()
    {
        // Every probe after the second keeps saying "everything failed". If that
        // counted as a fresh retirement the log would fill with warnings and the
        // flag would mean nothing.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.False(availability.Record(new List<MonitorProbe> { Dead() }));
        Assert.False(availability.JustRetired);
        Assert.True(availability.Retired);
    }

    [Fact]
    public void Turning_the_switch_on_gives_the_feature_its_two_rounds_again()
    {
        // The way back. Without a reset the very next failed probe would retire
        // it again and the user would never get a second attempt at all.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        availability.Restore();

        Assert.False(availability.Retired);
        Assert.Equal(0, availability.ConsecutiveRounds);
        Assert.False(availability.Record(new List<MonitorProbe> { Dead() }));
    }

    [Fact]
    public void A_retired_machine_keeps_its_retired_state_across_a_probe()
    {
        // Restore is only called when the user moves the switch. A background probe
        // must not quietly un-retire it.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        availability.ResetCount();

        Assert.True(availability.Retired);
    }

    [Fact]
    public void The_note_explains_the_turn_off_without_prescribing_a_fix()
    {
        // Never silent: a switch that changes itself and says nothing is
        // indistinguishable from a bug. And never a numbered list of settings to
        // change, because the steps it used to name were for a graphics setting
        // that was already correct, and following them changed nothing.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead(), Dead() });
        availability.Record(new List<MonitorProbe> { Dead(), Dead() });

        string note = availability.Note;

        Assert.Contains("turned this off", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1.", note, StringComparison.Ordinal);
        Assert.DoesNotContain("2.", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_does_not_tell_the_user_how_to_undo_something_they_can_see()
    {
        // This dialog is opened by a badge sitting on the switch it is about, and
        // the switch is off. "You can turn this back on" is a restatement of the
        // screen, and it was the last line of a dialog that had grown to three
        // paragraphs.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.DoesNotContain("try again", availability.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turn it back on", availability.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_note_is_empty_until_something_has_been_retired()
    {
        // A permanently reserved line in the settings list for a message almost
        // nobody will ever see is a gap in the list.
        Assert.Equal(string.Empty, new BacklightAvailability().Note);
    }

    [Fact]
    public void The_note_blames_the_driver_rather_than_the_display()
    {
        // Which is the whole finding. The panels are fine; the link is missing.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.Contains("driver", availability.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_note_does_not_condemn_the_display_hardware()
    {
        // "It failed" reads as a verdict on the monitor, and a panel that was merely
        // asleep when it was asked looks exactly the same as one that is broken.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.DoesNotContain("your displays do not support", availability.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_note_says_how_many_displays_were_tried()
    {
        // "Nothing worked" and "none of your two displays worked" are different
        // sentences, and the second is the one that tells somebody they have a
        // multi-monitor setup worth looking at.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead(), Dead(@"\\.\DISPLAY3") });
        availability.Record(new List<MonitorProbe> { Dead(), Dead(@"\\.\DISPLAY3") });

        Assert.Contains("2 displays", availability.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_reads_naturally_for_a_single_display()
    {
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.Contains("1 display", availability.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_is_short_enough_not_to_be_the_problem_it_explains()
    {
        // Two sentences, no list, no closing paragraph. This was 700 characters and
        // an amber warning triangle describing a switch the reader is looking at,
        // and the longer it grew the more it read as a fault rather than a note.
        // Pinned so a future edit cannot quietly reintroduce the wall of text.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        Assert.True(
            availability.Note.Length < 400,
            "the dialog copy has grown: " + availability.Note.Length + " chars");
    }

    [Fact]
    public void The_note_is_at_most_two_paragraphs()
    {
        // The dialog is 420 wide with the body wrapped, so length is what pushes
        // text past the bottom of the card. One break, so two paragraphs, is the
        // whole budget: what was measured, and what it usually means.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        availability.Record(new List<MonitorProbe> { Dead() });

        string note = availability.Note;

        Assert.Equal(1, note.Split("\n\n").Length - 1);
        Assert.Equal(note.TrimEnd(), note);
    }

    [Fact]
    public void A_missing_list_is_rejected_rather_than_treated_as_no_displays()
    {
        Assert.Throws<ArgumentNullException>(() => new BacklightAvailability().Record(null!));
    }

    /// <summary>
    /// The two-round rule only works if two rounds can actually happen, and this is
    /// the bug that stopped them.
    /// <para>
    /// The probe runs at most once per launch, guarded so a second open of the
    /// Display tab does not start another. A counter kept in memory could
    /// therefore only ever reach one, and a rule that retires after two was a rule
    /// that never fired: the note explaining the automatic turn-off could never
    /// appear on any machine, ever. The count and the retired flag are persisted
    /// so a round can span a launch.
    /// </para>
    /// </summary>
    [Fact]
    public void The_count_survives_a_restart_so_two_rounds_are_reachable()
    {
        var first = new BacklightAvailability();
        first.Record(new List<MonitorProbe> { Dead() });

        Assert.Equal(1, first.Persistable().Rounds);

        // Next launch: a brand new object, seeded from settings.
        var second = new BacklightAvailability();
        second.RestoreFrom(first.Persistable().Rounds, first.Persistable().Retired);

        Assert.Equal(1, second.ConsecutiveRounds);
        Assert.True(second.Record(new List<MonitorProbe> { Dead() }));
        Assert.True(second.Retired);
    }

    [Fact]
    public void The_retired_flag_survives_a_restart_so_the_note_can_explain_itself()
    {
        // The other half of the same bug. Even with a reachable threshold, an
        // in-memory flag would mean the switch is off on the next launch with no
        // way for the settings tab to say why, which is the silent change the note
        // exists to prevent.
        var first = new BacklightAvailability();
        first.Record(new List<MonitorProbe> { Dead() });
        first.Record(new List<MonitorProbe> { Dead() });

        var second = new BacklightAvailability();
        second.RestoreFrom(first.Persistable().Rounds, first.Persistable().Retired);

        Assert.True(second.Retired);
        Assert.NotEqual(string.Empty, second.Note);
    }

    /// <summary>
    /// The display count is in memory only, so on a fresh launch it reads zero and
    /// the sentence would claim "none of your 0 displays".
    /// <para>
    /// Found by a test written for a different reason: the retirement test rebuilt
    /// the object from settings the way a restart does, and the note it produced
    /// said "0 displays". The count is for the log and the dialog, and a dialog
    /// that names no displays is worse than one that says nothing about the number.
    /// </para>
    /// </summary>
    [Fact]
    public void A_restarted_reading_never_says_it_tried_zero_displays()
    {
        var first = new BacklightAvailability();
        first.Record(new List<MonitorProbe> { Dead(), Dead(@"\\.\DISPLAY3") });
        first.Record(new List<MonitorProbe> { Dead(), Dead(@"\\.\DISPLAY3") });

        var second = new BacklightAvailability();
        second.RestoreFrom(first.Persistable().Rounds, first.Persistable().Retired);

        string note = second.Note;

        Assert.DoesNotContain("0 displays", note, StringComparison.Ordinal);
        Assert.DoesNotContain("your 0 ", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_display_count_is_reported_from_the_current_probe_not_a_stale_one()
    {
        // A machine that gained a display between two launches would otherwise be
        // told about the number it had the first time.
        var first = new BacklightAvailability();
        first.Record(new List<MonitorProbe> { Dead() });
        first.Record(new List<MonitorProbe> { Dead() });

        var second = new BacklightAvailability();
        second.RestoreFrom(first.Persistable().Rounds, first.Persistable().Retired);
        second.Record(new List<MonitorProbe> { Dead(), Dead(@"\\.\DISPLAY3"), Dead(@"\\.\DISPLAY4") });

        Assert.Equal(3, second.LastRoundDisplays);
    }

    [Fact]
    public void A_restart_that_still_keeps_working_does_not_accumulate_a_streak()
    {
        // The counter is only ever incremented by a round that found nothing. A
        // round that reached a display has to clear it, or a machine that worked
        // once and then failed once would retire on the strength of a failure that
        // is not consecutive with anything.
        var availability = new BacklightAvailability();
        availability.Record(new List<MonitorProbe> { Dead() });
        (int rounds, bool retired) = availability.Persistable();

        var next = new BacklightAvailability();
        next.RestoreFrom(rounds, retired);
        next.Record(new List<MonitorProbe> { Live() });

        Assert.Equal(0, next.Persistable().Rounds);
        Assert.False(next.Retired);
    }

    [Fact]
    public void A_negative_stored_count_is_clamped_rather_than_counted_up_from_it()
    {
        // Settings are a file, and a file can hold anything. A count of minus one
        // would need three failures to retire rather than two.
        var availability = new BacklightAvailability();
        availability.RestoreFrom(-5, false);

        Assert.Equal(0, availability.ConsecutiveRounds);
    }

    [Fact]
    public void The_note_can_survive_a_restart_on_its_own()
    {
        // The exact situation a user is in: the app turned itself off yesterday
        // and they are opening it today. Nothing has been probed this session and
        // the line still has to be there.
        var availability = new BacklightAvailability();
        availability.RestoreFrom(2, retired: true);

        Assert.NotEqual(string.Empty, availability.Note);
    }

    [Fact]
    public void The_threshold_is_two_and_not_one()
    {
        // Pinned because it is a policy number that could be nudged by anyone
        // who has not watched a display fail once for a good reason.
        Assert.Equal(2, BacklightAvailability.RoundsBeforeRetiring);
    }
}
