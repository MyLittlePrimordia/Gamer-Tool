using System;

namespace GamerTool.Services;

/// <summary>
/// How strongly the night filter should be running, as a number from nothing to
/// full.
/// <para>
/// Deliberately nothing but time arithmetic, for the same reason
/// <see cref="NightSchedule"/> is: no WPF, no settings, no display calls, so the
/// part that is genuinely hard - whether the window is open right now, and what
/// happens either side of midnight - can be answered by a test. Midnight is the
/// seam that question is usually wrong about, and it is the reason this is a
/// separate file rather than three lines inside the schedule.
/// </para>
/// <para>
/// This does not change when the filter is on. <see cref="NightSchedule.IsActiveAt"/>
/// still decides that, unchanged, and this only says how far into it the current
/// moment is. A schedule that switches on instantly stays instant when the fade is
/// zero, so nothing about the existing behaviour depends on this existing.
/// </para>
/// </summary>
public static class NightFade
{
    /// <summary>
    /// How long the default fade runs for, in minutes. Thirty is chosen so that the
    /// evening's first few minutes are barely different and the visible part of the
    /// change is spread across getting up to the desk rather than happening while
    /// walking into the room.
    /// </summary>
    public const int DefaultFadeMinutes = 30;

    /// <summary>
    /// The longest fade worth offering, as a clamp on the setting.
    /// <para>
    /// A fade longer than this stops being a fade. Half the day is the shortest
    /// sensible night window, and a fade longer than the window itself means the
    /// filter never reaches full strength at all - which is not what somebody asking
    /// for a long fade wants, and is why the strength calculation below also clamps
    /// against the window length rather than trusting this number.
    /// </para>
    /// </summary>
    public const int MaxFadeMinutes = 120;

    /// <summary>
    /// The strength at <paramref name="now"/>, from 0 outside the schedule to 1 once
    /// the window has been open long enough.
    /// </summary>
    /// <param name="enabled">Whether the schedule is switched on at all.</param>
    /// <param name="startMinutes">Window start, minutes since midnight.</param>
    /// <param name="endMinutes">Window end, minutes since midnight.</param>
    /// <param name="now">The moment being asked about.</param>
    /// <param name="fadeMinutes">Fade length. Zero or less means no fade at all.</param>
    /// <remarks>
    /// A window that crosses midnight is the ordinary case - 20:00 to 07:00 is the
    /// shipped default - and it is handled here rather than by two callers testing
    /// either side of the seam separately, because a pair of comparisons joined with
    /// an "or" gets the join wrong: a window written as "now &gt; start" and a morning
    /// one written as "now &lt; end" leaves a gap either side of midnight, which is
    /// the part of the night it exists for.
    /// </remarks>
    public static double Strength(
        bool enabled,
        int startMinutes,
        int endMinutes,
        TimeSpan now,
        int fadeMinutes)
    {
        // Asked of the schedule rather than answered here. "Is the window open" and
        // "how far into it are we" are two questions, and duplicating the first would
        // be a second copy of the midnight logic to get wrong separately.
        if (!NightSchedule.IsActiveAt(enabled, startMinutes, endMinutes, now))
        {
            return 0.0;
        }

        if (fadeMinutes <= 0)
        {
            return 1.0;
        }

        int start = NightSchedule.SnapToStep(startMinutes);
        int end = NightSchedule.SnapToStep(endMinutes);

        // Modulo rather than a subtraction, so a window from 20:00 to 07:00 comes out
        // as 660 minutes rather than negative 780. This is the line that makes the
        // whole thing work across midnight.
        int length = ((end - start) + NightSchedule.MinutesPerDay) % NightSchedule.MinutesPerDay;

        // Equal start and end is either a filter meant to run all day or two boxes
        // filled in without noticing, and IsActiveAt has already answered "on" for
        // both. There is no edge to fade from, so it is full strength throughout.
        if (length == 0)
        {
            return 1.0;
        }

        double minutesNow = now.TotalMinutes;
        double sinceStart = Mod(minutesNow - start);
        double untilEnd = length - sinceStart;

        // Clamped to half the window. A fade longer than the distance between the two
        // edges would have the two ramps cross over, and taking the minimum of them
        // would produce a shape that goes up, comes back down, and goes up again -
        // a filter that dims itself in the middle of the night. Half the window is the
        // longest fade that leaves a genuine flat section in between.
        double fade = Math.Min(fadeMinutes, length / 2.0);

        return Math.Min(Clamp01(sinceStart / fade), Clamp01(untilEnd / fade));
    }

    /// <summary>
    /// A distance forwards around the clock, so "two hours before 8 PM" comes out as
    /// 12000 rather than -120.
    /// </summary>
    private static double Mod(double minutes) =>
        (minutes % NightSchedule.MinutesPerDay + NightSchedule.MinutesPerDay) % NightSchedule.MinutesPerDay;

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}