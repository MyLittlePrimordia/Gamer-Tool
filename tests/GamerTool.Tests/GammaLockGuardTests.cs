using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Whether the gamma lock has anything to defend.
/// <para>
/// The lock re-pushes the ramp on a timer, because a fullscreen game, a driver
/// reset or a competing gamma tool can put something else on screen. It used to
/// do that from the moment the app opened, whether or not Gamer Tool had ever
/// changed anything, which meant it spent its whole life writing a flat ramp over
/// whatever the user had set and re-flattening the ramp a reset had just
/// restored.
/// </para>
/// <para>
/// The rule is that the lock only holds a ramp this app actually landed.
/// <c>IsEnabled</c> is set by a successful apply and cleared by a reset, so it
/// is exactly the right answer to that question - and neither half of the test
/// here touches a display, because a real apply would push a real gamma ramp.
/// </para>
/// </summary>
public class GammaLockGuardTests
{
    [Fact]
    public void A_service_that_has_never_applied_anything_defends_nothing()
    {
        DisplayService display = new();

        // The lock is on by default, so this is the whole bug in one line: the
        // timer had something to do and nothing to defend it with.
        Assert.True(display.IsLockOn);
        Assert.False(display.IsEnabled);
        Assert.False(display.ShouldDefend);
    }

    [Fact]
    public void The_lock_defends_a_ramp_that_actually_landed()
    {
        DisplayService display = new();
        display.MarkAppliedForTest();

        // This is the lock doing its real job: holding the ramp against a
        // fullscreen game or a driver reset.
        Assert.True(display.ShouldDefend);
    }

    [Fact]
    public void Turning_the_lock_off_stops_it_defending_a_ramp_it_is_holding()
    {
        DisplayService display = new();
        display.MarkAppliedForTest();

        display.SetLock(false);

        Assert.False(display.ShouldDefend);
    }

    [Fact]
    public void A_reset_leaves_nothing_to_defend_so_the_ramp_that_was_restored_stays()
    {
        DisplayService display = new();
        display.MarkAppliedForTest();
        Assert.True(display.ShouldDefend);

        // Reset puts the original ramp back and stands IsEnabled down. This is
        // the case that used to break: the very next tick wrote a flat ramp over
        // what had just been restored, so a reset was undone about three seconds
        // later for anyone whose original ramp was not already flat.
        display.Reset();

        Assert.False(display.IsEnabled);
        Assert.False(display.ShouldDefend);
    }
}
