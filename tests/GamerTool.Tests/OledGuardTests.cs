using System;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The rule behind the OLED guard, and the reading on the tab that goes with it.
/// <para>
/// The Win32 part - asking Windows when it last saw an input event - cannot be tested
/// here, and does not need to be: it is a call that returns a number. Everything that
/// decides whether the crosshair should be on the screen is in these two functions and
/// is checked as a table.
/// </para>
/// </summary>
public class OledGuardTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Long = TimeSpan.FromMinutes(OledGuard.IdleMinutes + 1);

    [Fact]
    public void It_rests_only_when_it_is_on_nothing_is_playing_and_the_user_has_gone()
    {
        Assert.True(OledGuard.ShouldRest(enabled: true, visible: true, gameFullscreen: false, Long));
    }

    [Fact]
    public void The_switch_being_off_means_nothing_ever_happens()
    {
        Assert.False(OledGuard.ShouldRest(enabled: false, visible: true, gameFullscreen: false, Long));
    }

    [Fact]
    public void There_is_nothing_to_rest_while_no_crosshair_is_up()
    {
        Assert.False(OledGuard.ShouldRest(enabled: true, visible: false, gameFullscreen: false, Long));
    }

    [Fact]
    public void A_game_on_the_screen_is_never_interrupted()
    {
        // A pad player reads as idle to GetLastInputInfo, so a fullscreen game has to
        // override the whole rule or the crosshair vanishes mid-round.
        Assert.False(OledGuard.ShouldRest(enabled: true, visible: true, gameFullscreen: true, Long));
    }

    [Fact]
    public void The_idle_period_has_to_be_reached_and_not_merely_approached()
    {
        Assert.False(OledGuard.ShouldRest(enabled: true, visible: true, gameFullscreen: false, Short));
        Assert.False(OledGuard.ShouldRest(
            enabled: true, visible: true, gameFullscreen: false, TimeSpan.FromMinutes(OledGuard.IdleMinutes - 1)));
        Assert.True(OledGuard.ShouldRest(
            enabled: true, visible: true, gameFullscreen: false, TimeSpan.FromMinutes(OledGuard.IdleMinutes)));
    }

    [Fact]
    public void A_crosshair_the_guard_took_away_comes_back_only_when_someone_is_back()
    {
        Assert.False(OledGuard.ShouldWake(rested: false, Short));
        Assert.False(OledGuard.ShouldWake(rested: true, Long));
        Assert.True(OledGuard.ShouldWake(rested: true, TimeSpan.Zero));
    }

    [Fact]
    public void The_reading_on_the_tab_counts_up_in_minutes_and_then_hours()
    {
        var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("0m", OledGuard.OnScreenFor(null, start));
        Assert.Equal("<1m", OledGuard.OnScreenFor(start, start.AddSeconds(40)));
        Assert.Equal("12m", OledGuard.OnScreenFor(start, start.AddMinutes(12.7)));
        Assert.Equal("1h 0m", OledGuard.OnScreenFor(start, start.AddMinutes(60)));
        Assert.Equal("2h 5m", OledGuard.OnScreenFor(start, start.AddMinutes(125)));
    }

    [Fact]
    public void A_clock_that_has_gone_backwards_reads_as_no_time_at_all()
    {
        // A machine waking from sleep, or a system clock change, must not produce a
        // negative reading on the tab.
        var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        // Clamped to nothing rather than counted backwards, so the reading stays a
        // plausible "just went up" instead of a negative age.
        Assert.Equal("<1m", OledGuard.OnScreenFor(start, start.AddMinutes(-30)));
    }

    [Fact]
    public void The_idle_period_is_a_quarter_of_an_hour()
    {
        Assert.Equal(15, OledGuard.IdleMinutes);
    }
}
