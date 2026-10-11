using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// How hard the night filter should be running, as a function of the clock.
/// <para>
/// The schedule switches on instantly, which is the point of the complaint this
/// answers: at 8 PM the whole screen jumps to extra warm and stays there all night.
/// A fade spends the first half hour easing in.
/// </para>
/// <para>
/// Midnight is where this is easy to get wrong. The shipped default runs 20:00 to
/// 07:00, so the interesting half of every night's behaviour is on the far side of
/// the seam from the one the user typed into. That is why the whole shape of a
/// window is tested at several points rather than just its edges.
/// </para>
/// <para>
/// These assert Strength() only. Nothing here touches WPF, settings or the display,
/// which is what makes the midnight question answerable at all.
/// </para>
/// </summary>
public class NightFadeTests
{
    private const int DefaultStart = NightSchedule.DefaultStartMinutes;   // 20:00
    private const int DefaultEnd = NightSchedule.DefaultEndMinutes;       // 07:00

    private const int Fade = NightFade.DefaultFadeMinutes;               // 30

    private static TimeSpan At(int hour, int minute = 0) =>
        TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(minute);

    private static double DefaultWindow(int hour, int minute = 0) =>
        NightFade.Strength(true, DefaultStart, DefaultEnd, At(hour, minute), Fade);

    [Fact]
    public void NothingBeforeTheWindowOpens()
    {
        // Outside the schedule entirely, so no question of strength arises. 0 rather
        // than a negative: the tint is multiplied by this and a negative would invert
        // the trim.
        Assert.Equal(0.0, DefaultWindow(19, 59));
    }

    [Fact]
    public void NothingTheMorningAfter()
    {
        // The far side of midnight. Same reason as above, reached the other way round.
        Assert.Equal(0.0, DefaultWindow(7));
        Assert.Equal(0.0, DefaultWindow(7, 1));
        Assert.Equal(0.0, DefaultWindow(12));
    }

    [Fact]
    public void NothingWhenTheScheduleIsOff()
    {
        // Whether the boxes are right is irrelevant when the switch is off, and this
        // has to be checked before the window is read at all - a profile with
        // nonsense times must not be able to turn the filter on.
        Assert.Equal(0.0, NightFade.Strength(false, DefaultStart, DefaultEnd, At(22), Fade));
    }

    [Fact]
    public void TheWindowOpensAtNothing()
    {
        // The ramp starts at zero, not part way up. Starting at any other value would
        // be a jump to exactly the thing the fade exists to avoid.
        Assert.Equal(0.0, DefaultWindow(20));
    }

    [Fact]
    public void TheWindowClosesAtNothing()
    {
        // The end is exclusive, as IsActiveAt has always defined it - setting both
        // boxes to the same time must not produce a window that is never open. So
        // 07:00 itself is outside, and the fade has already reached zero by the time
        // the schedule says stop.
        Assert.Equal(0.0, DefaultWindow(7));
    }

    [Fact]
    public void TheLastMomentBeforeClosingIsNearlyNothingRatherThanFullyOn()
    {
        // A minute before the end, with a 30 minute fade, the remaining distance is
        // 1/30 of the ramp. Not zero - the filter has not stopped yet - and not one.
        // Asserted because a fade that snapped to full at any point before the edge
        // would be worse than no fade at all.
        double strength = DefaultWindow(6, 59);

        Assert.True(strength > 0.0, "the filter stopped a minute early");
        Assert.True(strength < 0.1, "the filter was still nearly full a minute before closing: " + strength);
    }

    [Fact]
    public void HalfwayThroughTheFadeInIsHalf()
    {
        Assert.Equal(0.5, DefaultWindow(20, 15), 3);
    }

    [Fact]
    public void HalfwayThroughTheFadeOutIsHalf()
    {
        // Measured back from the 07:00 close, not forward from midnight: the last
        // half hour of fade is 06:30 to 07:00, so the middle of it is 06:45.
        Assert.Equal(0.5, DefaultWindow(6, 45), 3);
    }

    [Fact]
    public void TheFadeOutIsMeasuredFromTheEndNotFromMidnight()
    {
        // The mistake this pins down: reading the fade-out as "halfway through the
        // evening" rather than as "half the fade before the window closes". 06:30 is
        // 30 minutes before the close, so it is still at full strength, not half.
        Assert.Equal(1.0, DefaultWindow(6, 30), 3);
        Assert.Equal(0.5, DefaultWindow(6, 45), 2);

        // Each minute closer to the close is strictly weaker, and the last one is
        // nearly nothing. A ramp measured from midnight instead would have been flat
        // at one all the way to 06:59 and then dropped, which is the snap this is
        // meant to remove.
        // The decline starts at 06:30, not at 06:00: the last 30 minutes of the window
        // are the fade-out and everything before that is flat. Sampled from 06:30 so a
        // flat stretch is not mistaken for a failure to decline.
        double previous = 2.0;
        for (int minute = 30; minute <= 60; minute++)
        {
            double strength = DefaultWindow(6, minute);
            Assert.True(strength < previous, "strength did not fall at 06:" + minute);
            previous = strength;
        }

        Assert.True(previous < 0.05, "the last minute before closing was " + previous);
    }

    [Fact]
    public void FullStrengthBetweenTheTwoFades()
    {
        // The flat section. Without it a fade long enough to overlap would leave the
        // filter never reaching what the user asked for.
        Assert.Equal(1.0, DefaultWindow(21), 3);
        Assert.Equal(1.0, DefaultWindow(23), 3);
        Assert.Equal(1.0, DefaultWindow(3), 3);
        Assert.Equal(1.0, DefaultWindow(5, 30), 3);
    }

    [Fact]
    public void StrengthNeverLeavesTheRange()
    {
        // Swept across the whole day rather than sampled, because a wrong sign or a
        // missed modulo shows up in one direction and not the other.
        for (int minute = 0; minute < NightSchedule.MinutesPerDay; minute++)
        {
            double strength = DefaultWindow(minute / 60, minute % 60);
            Assert.InRange(strength, 0.0, 1.0);
        }
    }

    [Fact]
    public void TheFadeIsSymmetricAboutTheMiddleOfTheWindow()
    {
        // 20:00 to 07:00 is 660 minutes, so the middle is at 01:30. Equal distances
        // either side of it must give equal strength, which is what "takes the
        // smaller of the two ramps" means.
        int middle = DefaultStart + ((DefaultEnd - DefaultStart + NightSchedule.MinutesPerDay) % NightSchedule.MinutesPerDay) / 2;
        Assert.Equal(1.0, NightFade.Strength(true, DefaultStart, DefaultEnd, At(middle / 60, middle % 60), Fade), 3);

        for (int offset = 1; offset <= 60; offset += 10)
        {
            double before = DefaultWindow((middle - offset) / 60, (middle - offset) % 60);
            double after = DefaultWindow((middle + offset) / 60, (middle + offset) % 60);
            Assert.Equal(before, after, 3);
        }
    }

    [Fact]
    public void NoFadeMeansFullStrengthTheMomentTheWindowOpens()
    {
        // Zero is the switch back to today's behaviour, so it has to be exact rather
        // than approximately instant. This is what makes the feature safe to turn off.
        for (int minute = 0; minute < NightSchedule.MinutesPerDay; minute++)
        {
            double strength = NightFade.Strength(true, DefaultStart, DefaultEnd, At(minute / 60, minute % 60), 0);
            Assert.InRange(strength, 0.0, 1.0);

            bool inside = NightSchedule.IsActiveAt(true, DefaultStart, DefaultEnd, At(minute / 60, minute % 60));
            Assert.Equal(inside ? 1.0 : 0.0, strength);
        }
    }

    [Fact]
    public void ANegativeFadeIsTreatedAsNoFadeRatherThanAsAnError()
    {
        // settings.json is editable by hand and a restore can bring back a value
        // written by a build that meant something else. Throwing here would take the
        // whole app down over a number, and the safe reading of a nonsense fade is
        // the old behaviour.
        Assert.Equal(1.0, NightFade.Strength(true, DefaultStart, DefaultEnd, At(22), -30));
    }

    [Fact]
    public void AWindowShorterThanTwoFadesStillPeaks()
    {
        // A one hour window with a 30 minute fade cannot reach full strength and come
        // back down without crossing over. Taking the minimum of the two ramps means
        // it peaks at exactly the midpoint instead - weaker than asked for, but the
        // shape stays sane and the filter is never weaker at the edges than in the
        // middle.
        double peak = 0.0;
        for (int minute = 0; minute <= 60; minute++)
        {
            peak = Math.Max(peak, NightFade.Strength(true, 22 * 60, 23 * 60, At(22, minute), Fade));
        }

        Assert.Equal(1.0, peak, 3);
    }

    [Fact]
    public void AWindowShorterThanItsFadeNeverDipsInTheMiddle()
    {
        // The failure this clamping exists to prevent. With a 30 minute window and a
        // two hour fade, the ramp up and the ramp down would cross: the filter would
        // climb, fall back toward nothing, then climb again - dimmest in the middle of
        // a night somebody asked to be warm.
        double previous = 0.0;
        for (int minute = 0; minute <= 30; minute++)
        {
            double strength = NightFade.Strength(true, 22 * 60, 22 * 60 + 30, At(22, minute), NightFade.MaxFadeMinutes);
            Assert.True(
                strength >= previous - 1e-9,
                "strength fell from " + previous.ToString("0.000")
                + " to " + strength.ToString("0.000") + " at minute " + minute);

            previous = strength;
        }
    }

    [Fact]
    public void AWindowAllDayStaysAtFullStrength()
    {
        // Equal start and end means "all day" rather than "never", which is what
        // IsActiveAt answers, so there is no edge to fade from.
        for (int hour = 0; hour < 24; hour += 3)
        {
            Assert.Equal(1.0, NightFade.Strength(true, 12 * 60, 12 * 60, At(hour), Fade));
        }
    }

    [Fact]
    public void AWindowThatDoesNotCrossMidnightBehavesTheSameWay()
    {
        // 09:00 to 17:00, sampled the same way as the default. The midnight handling
        // must not be the only thing being tested, or a sign error in the modulo could
        // pass every test here and still break the ordinary case.
        Assert.Equal(0.0, NightFade.Strength(true, 9 * 60, 17 * 60, At(9), Fade));
        Assert.Equal(0.5, NightFade.Strength(true, 9 * 60, 17 * 60, At(9, 15), Fade), 3);
        Assert.Equal(1.0, NightFade.Strength(true, 9 * 60, 17 * 60, At(13), Fade), 3);
        Assert.Equal(0.5, NightFade.Strength(true, 9 * 60, 17 * 60, At(16, 45), Fade), 3);
        Assert.Equal(0.0, NightFade.Strength(true, 9 * 60, 17 * 60, At(17), Fade));
    }

    [Fact]
    public void TheSameWindowNeverGivesTwoDifferentAnswers()
    {
        // Minutes past midnight and a TimeSpan built from them have to agree exactly.
        // The schedule compares against (int)now.TotalMinutes in some paths and a
        // TimeSpan in others, and a mismatch between them would show up as a filter
        // that flickers for one tick around an edge.
        for (int minute = 0; minute < NightSchedule.MinutesPerDay; minute += 7)
        {
            TimeSpan span = TimeSpan.FromMinutes(minute);
            Assert.Equal(
                NightFade.Strength(true, DefaultStart, DefaultEnd, span, Fade),
                NightFade.Strength(true, DefaultStart, DefaultEnd, At(minute / 60, minute % 60), Fade));
        }
    }

    [Fact]
    public void MinutesOfDayFromTheSettingsAreSnappedBeforeTheyMatter()
    {
        // A profile hand-edited to 20:07 must behave as 20:00 rather than as an edge
        // the picker cannot show. SnapToStep is already doing this for the schedule;
        // what matters is that this did not accidentally reintroduce the unsnapped
        // value, which would put the fade on a different edge from the switch.
        Assert.Equal(
            NightFade.Strength(true, DefaultStart, DefaultEnd, At(21), Fade),
            NightFade.Strength(true, DefaultStart + 7, DefaultEnd, At(21), Fade));
    }

    [Fact]
    public void StrengthRisesMonotonicallyThroughTheFadeIn()
    {
        double previous = -1.0;
        for (int minute = 0; minute <= Fade; minute++)
        {
            double strength = NightFade.Strength(true, DefaultStart, DefaultEnd, At(20, minute), Fade);
            Assert.True(strength >= previous, "fell at minute " + minute);
            previous = strength;
        }

        Assert.Equal(1.0, previous, 3);
    }
}