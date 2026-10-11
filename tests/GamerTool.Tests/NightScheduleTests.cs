using System;
using System.IO;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The night schedule's arithmetic, on its own.
/// <para>
/// The whole feature is four lines of "is the current time inside this window"
/// and a great deal of care about the window, because the window is the part
/// that is wrong at midnight. Every one of these cases is a time of day that
/// either side of a boundary lands on, since a schedule that is fifteen minutes
/// out in either direction is the only bug this can really have.
/// </para>
/// <para>
/// Nothing here touches WPF, the settings file or the display service, which is
/// the point of keeping the question in a class of its own.
/// </para>
/// </summary>
public class NightScheduleTests
{
    private const int From8Pm = NightSchedule.DefaultStartMinutes;
    private const int Until7Am = NightSchedule.DefaultEndMinutes;

    private static TimeSpan At(int hours, int minutes = 0) => new(hours, minutes, 0);

    [Fact]
    public void SwitchedOffIsNeverOn()
    {
        // Every hour of the day, so an off schedule cannot be on at some
        // untested time of day.
        for (int hour = 0; hour < 24; hour++)
        {
            Assert.False(NightSchedule.IsActiveAt(false, From8Pm, Until7Am, At(hour)));
        }
    }

    [Fact]
    public void DefaultWindowIsTheEvening()
    {
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(20)));
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(23, 59)));
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(0)));
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(6, 59)));
    }

    [Fact]
    public void DefaultWindowIsNotTheDaytime()
    {
        // Midnight is not in here. 00:00 is half past one in the morning, and a
        // 20:00 to 07:00 window is open then. It is listed in the evening test
        // instead, because that is the part of the window people forget exists.
        Assert.False(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(7)));
        Assert.False(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(12)));
        Assert.False(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(19, 59)));
    }

    [Fact]
    public void StartIsInclusiveAndEndIsExclusive()
    {
        // 20:00:00 on, 07:00:00 off. Both of these are the case that decides
        // whether the filter blinks on for no time at the start of the evening
        // and for no reason at the end of the night.
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(20, 0)));
        Assert.False(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(7, 0)));
    }

    [Fact]
    public void EveningAndMorningHalvesJoinAcrossMidnight()
    {
        // The seam. A window written as two halves with an "or" between them
        // loses the last few minutes before midnight and the first few after,
        // which is the part of the night the schedule is for.
        for (int minute = 0; minute < 24 * 60; minute += 7)
        {
            bool expected = minute >= From8Pm || minute < Until7Am;
            bool actual = NightSchedule.IsActiveAt(true, From8Pm, Until7Am, new TimeSpan(0, minute, 0));
            Assert.True(
                expected == actual,
                "minute " + minute + " expected " + expected);
        }
    }

    [Fact]
    public void AWindowInsideOneDayDoesNotWrap()
    {
        int from = 9 * 60;
        int until = 17 * 60;

        Assert.False(NightSchedule.IsActiveAt(true, from, until, At(8, 59)));
        Assert.True(NightSchedule.IsActiveAt(true, from, until, At(9)));
        Assert.True(NightSchedule.IsActiveAt(true, from, until, At(12)));
        Assert.True(NightSchedule.IsActiveAt(true, from, until, At(16, 59)));
        Assert.False(NightSchedule.IsActiveAt(true, from, until, At(17)));
        Assert.False(NightSchedule.IsActiveAt(true, from, until, At(23, 59)));
    }

    [Fact]
    public void AWindowThatEndsWhereItBeginsIsAllDay()
    {
        // Either a filter meant to run always, or two boxes filled in the same
        // by accident. The screen is left warm in both readings, which is the
        // answer that is wrong in the harmless direction.
        for (int hour = 0; hour < 24; hour++)
        {
            Assert.True(NightSchedule.IsActiveAt(true, 0, 0, At(hour)));
            Assert.True(NightSchedule.IsActiveAt(true, 13 * 60, 13 * 60, At(hour)));
        }
    }

    [Fact]
    public void MidnightAndTheLastMinuteOfTheDayAreTreated()
    {
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(0, 0)));
        Assert.True(NightSchedule.IsActiveAt(true, From8Pm, Until7Am, At(23, 59)));
    }

    [Fact]
    public void OutOfRangeStoredTimesAreFoldedIntoADay()
    {
        // settings.json is editable by hand and a restore can bring back a value
        // written by a build that meant something else, so a bad number must not
        // produce a window that is never open or one that is always open.
        //
        // Both of these still describe a real window after folding: -1:00 is
        // midnight and 1:00 is one in the morning, so it is the hour to one. A
        // start and an end that fold onto each other is a different case, and is
        // the one below.
        Assert.True(NightSchedule.IsActiveAt(true, -60, 60, At(0, 30)));
        Assert.False(NightSchedule.IsActiveAt(true, -60, 60, At(1, 30)));

        // A time past the end of the day folds onto eleven at night, and paired
        // with a morning time that is a window that wraps midnight like any
        // other. Eleven to eight at night is the same shape as eight to seven
        // in the morning, and the arithmetic should not care which was typed.
        Assert.True(NightSchedule.IsActiveAt(true, 26 * 60, 8 * 60, At(23, 30)));
        Assert.False(NightSchedule.IsActiveAt(true, 26 * 60, 8 * 60, At(12)));
    }

    [Fact]
    public void TwoDamagedTimesThatCollapseOntoEachOtherMeanOff()
    {
        // Past the last hour of the day, everything folds onto eleven at night,
        // so two such times are no longer tellable apart. Taken after the fold
        // they would read as two boxes set to the same time, which is the
        // deliberate "run all day" case, and the screen would stay warm until
        // the app was closed. A filter nobody can turn off is worse than one
        // that does not come on.
        Assert.False(NightSchedule.IsActiveAt(true, 25 * 60, 26 * 60, At(12)));
        Assert.False(NightSchedule.IsActiveAt(true, 25 * 60, 26 * 60, At(23, 30)));
        Assert.False(NightSchedule.IsActiveAt(true, -120, -60, At(3)));

        // Which is not the same as the two being deliberately equal, above.
        Assert.True(NightSchedule.IsActiveAt(true, 25 * 60, 25 * 60, At(12)));

        // And the ordinary case still means all day rather than nothing, because
        // two boxes a user set to the same hour is a filter they meant to leave
        // running. The line between the two readings is whether the stored values
        // were equal, not whether they ended up equal.
        Assert.True(NightSchedule.IsActiveAt(true, 0, 0, At(0)));
        Assert.True(NightSchedule.IsActiveAt(true, 13 * 60, 13 * 60, At(3)));
    }

    [Theory]
    [InlineData(0, "12 AM")]
    [InlineData(60, "1 AM")]
    [InlineData(11 * 60, "11 AM")]
    [InlineData(12 * 60, "12 PM")]
    [InlineData(13 * 60, "1 PM")]
    [InlineData(20 * 60, "8 PM")]
    [InlineData(23 * 60, "11 PM")]
    [InlineData(-5, "12 AM")]
    [InlineData(99 * 60, "11 PM")]
    [InlineData(20 * 60 + 7, "8 PM")]
    public void MinutesFormatAsTwelveHourTimes(int minutes, string expected)
    {
        // The last three are the point. 99:00 and 20:07 are both values a
        // hand-edited settings.json can hold, and both used to come out as a
        // label with no match in the picker's list - which is a selection that
        // is dropped without complaint, and a box showing nothing.
        Assert.Equal(expected, NightSchedule.FormatMinutes(minutes));
    }

    [Fact]
    public void MidnightAnd_noon_are_both_a_twelve_and_only_the_meridiem_tells_them_apart()
    {
        // The one thing worth writing down. On the dial both are 12, and reading
        // it the other way round moves a schedule by twelve hours - which on a
        // filter that comes on in the evening is the difference between the
        // feature working and it never being on.
        Assert.Equal(0, Parse("12 AM"));
        Assert.Equal(12 * 60, Parse("12 PM"));

        Assert.Equal("12 AM", NightSchedule.FormatMinutes(0));
        Assert.Equal("12 PM", NightSchedule.FormatMinutes(12 * 60));

        // And they must never be the same string, or the picker would offer one
        // label for two different hours.
        Assert.NotEqual(NightSchedule.FormatMinutes(0), NightSchedule.FormatMinutes(12 * 60));
    }

    private static int Parse(string label)
    {
        Assert.True(NightSchedule.TryParseMinutes(label, out int minutes), label + " did not parse");
        return minutes;
    }

    [Fact]
    public void A_value_never_moves_later_than_it_was_put()
    {
        // Floored, not rounded. Rounding 23:30 would land on midnight and move a
        // schedule to the other end of the day.
        Assert.Equal(23 * 60, NightSchedule.SnapToStep(23 * 60 + 30));
        Assert.Equal(23 * 60, NightSchedule.SnapToStep(24 * 60 + 30));
        Assert.Equal(20 * 60, NightSchedule.SnapToStep(20 * 60 + 59));
        Assert.Equal(0, NightSchedule.SnapToStep(0));
    }

    [Theory]
    [InlineData("12 AM", 0)]
    [InlineData("7 AM", 420)]
    [InlineData("11 AM", 660)]
    [InlineData("12 PM", 720)]
    [InlineData("1 PM", 780)]
    [InlineData("8 PM", 1200)]
    [InlineData("11 PM", 1380)]
    public void TimesParseBackToMinutes(string text, int expected)
    {
        Assert.True(NightSchedule.TryParseMinutes(text, out int minutes));
        Assert.Equal(expected, minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("8")]
    [InlineData("8 PM 30")]
    [InlineData("20:00")]
    [InlineData("24:00")]
    [InlineData("0 AM")]
    [InlineData("13 PM")]
    [InlineData("-1 AM")]
    [InlineData("+8 PM")]
    [InlineData("eight PM")]
    [InlineData("8 P.M.")]
    public void UnreadableTimesAreRefusedRatherThanGuessed(string? text)
    {
        // Strict on purpose. A value that does not parse is a corrupt or foreign
        // profile, not a person being imprecise, and quietly reading it as
        // midnight would schedule the filter for the wrong twelve hours.
        Assert.False(NightSchedule.TryParseMinutes(text, out _));
    }

    [Fact]
    public void Lowercase_meridiem_is_the_same_hour()
    {
        // The picker is the only source of these strings and it writes them
        // upper case, so this cannot arrive from the UI. It can arrive from a
        // profile somebody edited, and refusing it would answer a formatting
        // quibble by losing somebody's schedule.
        Assert.Equal(Parse("8 PM"), Parse("8 pm"));
        Assert.Equal(Parse("7 AM"), Parse("7 am"));
    }

    [Fact]
    public void The_picker_is_one_whole_hour_at_a_time()
    {
        // Twenty four entries, which fits on a screen without a scrollbar. The
        // quarter hour version was ninety six of them, and scrolling a list to
        // find the time you are about to set is the friction that made the
        // control feel fiddly for something that is a habit, not an instrument.
        Assert.Equal(24, NightSchedule.TimeLabels.Count);
        Assert.Equal(60, NightSchedule.StepMinutes);
        Assert.Equal("12 AM", NightSchedule.TimeLabels[0]);
        Assert.Equal("12 PM", NightSchedule.TimeLabels[12]);
        Assert.Equal("11 PM", NightSchedule.TimeLabels[23]);

        foreach (string label in NightSchedule.TimeLabels)
        {
            Assert.True(NightSchedule.TryParseMinutes(label, out int minutes), label + " does not parse back");
            Assert.Equal(0, minutes % NightSchedule.StepMinutes);
        }
    }

    [Fact]
    public void Every_offered_time_round_trips_to_the_hour_it_names()
    {
        // The two halves of a combo have to agree or the selection is dropped,
        // so this asks the same question the assignment does: does every label
        // in the list mean the hour it displays?
        foreach (string label in NightSchedule.TimeLabels)
        {
            Assert.True(NightSchedule.TryParseMinutes(label, out int minutes));

            Assert.Equal(label, NightSchedule.FormatMinutes(minutes));
        }
    }

    [Fact]
    public void TheScheduleLevelIsTheStrongestTheAppAlreadyHas()
    {
        // Referenced rather than written as a number, so if the levels are ever
        // reordered this stops being EXTRA WARM without anybody noticing.
        Assert.Equal(2, NightSchedule.NightLevel);
        Assert.Equal(NightSchedule.NightLevel, Models.DisplayPreset.BlueLightNames.Length - 1);
    }

    [Fact]
    public void TheSchedulesLevelWinsOnlyWhileItIsRunning()
    {
        Assert.Equal(NightSchedule.NightLevel, NightSchedule.EffectiveLevel(true, 0));
        Assert.Equal(NightSchedule.NightLevel, NightSchedule.EffectiveLevel(true, 1));

        // The user's own level is untouched either way. This is the whole reason
        // the schedule does not write to the setting it applies: a filter that
        // saved over it would come back as EXTRA WARM for good after one night.
        Assert.Equal(0, NightSchedule.EffectiveLevel(false, 0));
        Assert.Equal(1, NightSchedule.EffectiveLevel(false, 1));
        Assert.Equal(2, NightSchedule.EffectiveLevel(false, 2));
    }

    [Fact]
    public void AFailingLevelIsClampedRatherThanThrown()
    {
        Assert.Equal(0, NightSchedule.EffectiveLevel(false, -3));
        Assert.Equal(NightSchedule.NightLevel, NightSchedule.EffectiveLevel(false, 9));
    }

    [Fact]
    public void The_pickers_are_given_something_to_pick_from()
    {
        // This one shipped broken. The two boxes were given a SelectedItem and
        // never given an ItemsSource, and a selection with nothing behind it is
        // dropped without complaint - so the row rendered as a pair of empty
        // bordered rectangles and the schedule had no times in it at all. Every
        // other test in this file passed while the feature was unusable.
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.xaml.cs")));

        Assert.Contains("NightStartBox.ItemsSource = NightSchedule.TimeLabels", source);
        Assert.Contains("NightEndBox.ItemsSource = NightSchedule.TimeLabels", source);
    }

    [Fact]
    public void A_stored_time_finds_its_own_match_in_the_picker()
    {
        // The two halves of a combo have to agree or the selection is dropped,
        // so this asks the same question the assignment does: does the stored
        // value produce a label that is actually in the list?
        foreach (int stored in new[] { 0, 15, 7 * 60, 12 * 60, 20 * 60, 23 * 60, 99 * 60, -30 })
        {
            string label = NightSchedule.FormatMinutes(stored);

            Assert.Contains(label, NightSchedule.TimeLabels);
            Assert.True(NightSchedule.TryParseMinutes(label, out int back), label + " does not parse back");
            Assert.Equal(NightSchedule.SnapToStep(stored), back);
        }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void TheDefaultsAreTheOnesTheRowOpensWith()
    {
        Models.AppSettings settings = new();

        Assert.Equal(20 * 60, settings.NightStartMinutes);
        Assert.Equal(7 * 60, settings.NightEndMinutes);
        Assert.False(settings.NightBlueLight);

        // Off until asked for. A screen that warmed itself on launch would be
        // the app doing something to somebody who has never heard of it.
        Assert.False(NightSchedule.IsActiveAt(
            settings.NightBlueLight, settings.NightStartMinutes, settings.NightEndMinutes, At(22)));

        // Eight in the evening to seven in the morning, which is what the row
        // reads when it opens. Late enough that it is not on through a normal
        // evening, early enough that the morning is not still warm.
        Assert.Equal("8 PM", NightSchedule.FormatMinutes(settings.NightStartMinutes));
        Assert.Equal("7 AM", NightSchedule.FormatMinutes(settings.NightEndMinutes));
    }
}
