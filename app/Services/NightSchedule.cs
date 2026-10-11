using System;
using System.Collections.Generic;
using System.Globalization;

namespace GamerTool.Services;

/// <summary>
/// The hours the night blue light filter runs for, and the question "is it on
/// right now".
/// <para>
/// Deliberately nothing but time arithmetic. No WPF, no settings object, no
/// display calls, so the only genuinely hard part of the feature - whether the
/// window is open right now, and what happens either side of midnight - can be
/// answered by a test, and the window is left as a dumb caller of it.
/// </para>
/// <para>
/// This is a schedule for the app's own blue light filter and nothing else. It
/// does not drive Windows' Night Light: that is stored in undocumented
/// CloudStore blobs whose layout changes between builds, which is the opposite
/// of the kind of API this app is allowed to stand on.
/// </para>
/// </summary>
public static class NightSchedule
{
    /// <summary>
    /// The level the schedule puts on: EXTRA WARM, the strongest the app
    /// already offers.
    /// <para>
    /// A level of its own would be a third thing to choose between, a control to
    /// explain and a second value to get wrong. A night filter is for getting
    /// through the evening, and the app already ships a level for that.
    /// </para>
    /// </summary>
    public const int NightLevel = 2;

    /// <summary>20:00. Late enough that it is not on through a normal evening.</summary>
    public const int DefaultStartMinutes = 20 * 60;

    /// <summary>07:00. Early enough that the morning is not still warm.</summary>
    public const int DefaultEndMinutes = 7 * 60;

    public const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// How fine the two pickers go, and the only reason anything snaps.
    /// <para>
    /// An hour. The first version of this was a quarter hour, on the grounds
    /// that finer is more flexible, and it turned two rows of 96 options into
    /// a pair of lists nobody scrolls. A schedule that turns the screen warm at
    /// eight is a habit, not an instrument: somebody who wants 19:45 is tuning
    /// something, and the thing they are tuning is the wrong end of the
    /// difference between amber and warm.
    /// </para>
    /// <para>
    /// It also makes the pickers a plain 24 entries, which is a list that fits on
    /// a screen without a scrollbar - so the value is visible rather than
    /// something you scroll to find.
    /// </para>
    /// </summary>
    public const int StepMinutes = 60;

    /// <summary>
    /// The two pickers' options, as "8 PM" and "12 AM".
    /// <para>
    /// Twelve hour rather than twenty four, because this is read at a glance in
    /// the evening by somebody deciding when to go to bed, and there is a real
    /// chance of the two being confused: on a twenty four hour clock "7" is
    /// morning and "19" is evening, and the one that matters is the evening.
    /// Strings rather than numbers so the control shows the value it is going to
    /// apply, with no converter between the stored minutes and what is on screen.
    /// <see cref="TryParseMinutes"/> is the way back.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> TimeLabels { get; } = BuildLabels();

    private static IReadOnlyList<string> BuildLabels()
    {
        List<string> labels = new(MinutesPerDay / StepMinutes);
        for (int minute = 0; minute < MinutesPerDay; minute += StepMinutes)
        {
            labels.Add(FormatMinutes(minute));
        }

        return labels;
    }

    /// <summary>
    /// A time of day folded onto one the picker can actually show.
    /// <para>
    /// Clamped to the day and then floored onto <see cref="StepMinutes"/>,
    /// rather than only clamped. settings.json is editable by hand and a restore
    /// can bring back a value written by a build that meant something else, and
    /// a value the picker does not offer is a value with no match in its list -
    /// so assigning it to the selection drops it without complaint and the box
    /// just shows nothing. That is the same blank control this method exists to
    /// prevent, reached by the other door.
    /// </para>
    /// <para>
    /// Floored rather than rounded, so a value never moves later than the user
    /// put it. Rounding 23:30 would land on midnight and move a schedule to the
    /// other end of the day.
    /// </para>
    /// </summary>
    public static int SnapToStep(int minutes) =>
        Math.Clamp(minutes, 0, MinutesPerDay - 1) / StepMinutes * StepMinutes;

    /// <summary>
    /// Minutes since midnight, as the picker spells it.
    /// <para>
    /// Midnight is 12 AM and noon is 12 PM, which is the part that is worth
    /// writing down: both are the same 12 on the dial, and only the meridiem
    /// tells them apart. Getting it the other way round moves a schedule by
    /// twelve hours, which on a filter that comes on in the evening is the
    /// difference between the feature working and it never being on.
    /// </para>
    /// </summary>
    public static string FormatMinutes(int minutes) => FormatHour(SnapToStep(minutes) / 60);

    /// <summary>An hour of the day, 0 to 23, as "12 AM" through "11 PM".</summary>
    public static string FormatHour(int hour)
    {
        int h = Math.Clamp(hour, 0, 23);

        string meridiem = h < 12 ? "AM" : "PM";
        return (h % 12 == 0 ? 12 : h % 12).ToString(CultureInfo.InvariantCulture) + " " + meridiem;
    }

    /// <summary>
    /// Reads "8 PM" back to minutes, refusing anything that is not exactly that.
    /// <para>
    /// Strict on purpose. This is a picker rather than a typed field, so a value
    /// that does not parse is not a user being imprecise, it is a corrupt or
    /// foreign profile, and quietly turning it into midnight would schedule the
    /// filter for the wrong twelve hours.
    /// </para>
    /// </summary>
    public static bool TryParseMinutes(string? text, out int minutes)
    {
        minutes = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
            || hour < 1 || hour > 12)
        {
            return false;
        }

        string meridiem = parts[1].ToUpperInvariant();
        if (meridiem is not ("AM" or "PM"))
        {
            return false;
        }

        // The 12 is the only hour that is not its own number, and it is 0 in the
        // morning and 12 in the afternoon. Reducing before adding is what makes
        // "12 AM" midnight rather than noon.
        int hourOfDay = (hour % 12) + (meridiem == "PM" ? 12 : 0);
        minutes = hourOfDay * 60;
        return true;
    }

    /// <summary>
    /// Whether the schedule says the filter should be on at <paramref name="now"/>.
    /// <para>
    /// The end is exclusive and the start is inclusive, so setting both boxes to
    /// the same time does not produce a window that is never open.
    /// </para>
    /// </summary>
    public static bool IsActiveAt(bool enabled, int startMinutes, int endMinutes, TimeSpan now)
    {
        if (!enabled)
        {
            return false;
        }

        int start = SnapToStep(startMinutes);
        int end = SnapToStep(endMinutes);
        int minute = (int)now.TotalMinutes;

        // The same time in both boxes is not a zero length window. It is either a
        // filter meant to run all day, or two boxes filled in without noticing,
        // and both readings are answered by staying on. The other answer is the
        // worse one: a schedule that is quietly off looks exactly like a broken
        // one, and the user has no way to tell which they are looking at.
        //
        // Asked of the stored values rather than of the clamped ones, which is the
        // whole reason it is here. Two minutes that are different but both past
        // the end of the day both clamp to 23:59, and taking the comparison after
        // the clamp turned a damaged profile into a filter that never turns off.
        if (startMinutes == endMinutes)
        {
            return true;
        }

        // Different, but no longer tellable apart once they are in range. Damaged
        // rather than deliberate, so off: a filter nobody can turn off is worse
        // than one that does not come on.
        if (start == end)
        {
            return false;
        }

        if (start < end)
        {
            return minute >= start && minute < end;
        }

        // Wraps midnight. 20:00 to 07:00 is the ordinary case, and the reason
        // this is one test rather than two is that a pair of comparisons joined
        // with an "or" gets the seam wrong: an evening window written as
        // "now > start" and a morning one written as "now < end" has a gap
        // either side of midnight, which is the part of the night it is for.
        return minute >= start || minute < end;
    }

    /// <summary>Whether the schedule is on for the wall clock time it is called at.</summary>
    public static bool IsActive(bool enabled, int startMinutes, int endMinutes) =>
        IsActiveAt(enabled, startMinutes, endMinutes, DateTime.Now.TimeOfDay);

    /// <summary>
    /// The level the blue light filter should actually be showing: the schedule's
    /// while it is running, the user's own choice at every other time.
    /// <para>
    /// This is why the schedule never writes over the user's level. The choice in
    /// the dropdown and the level on the screen are two different things, and a
    /// filter that overwrote the setting it is applying would come back as
    /// "EXTRA WARM" forever after the first night it was left on.
    /// </para>
    /// </summary>
    public static int EffectiveLevel(bool nightActive, int userLevel) =>
        nightActive ? NightLevel : Math.Clamp(userLevel, 0, NightLevel);
}
