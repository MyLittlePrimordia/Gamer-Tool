using System;
using GamerTool;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// How often a fault is allowed to interrupt.
/// <para>
/// The dispatcher handler marks an exception handled and carries on, which is
/// right, and then showed a modal for it. Several timers run continuously, so a
/// fault inside a tick handler produced a modal box on every tick: unmissable,
/// modal, and recurring the instant it was dismissed. Every occurrence still goes
/// to the log without limit - this is only about the box.
/// </para>
/// <para>
/// Each test uses its own fault text, because the cooldown state is deliberately
/// process wide and shared. A text nothing else has used is always due a dialog,
/// so these do not depend on the order they run in.
/// </para>
/// </summary>
public class FaultDialogThrottleTests
{
    private static readonly DateTime Start = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_occurrence_of_a_fault_is_shown()
    {
        Assert.True(App.TryBeginFaultDialog("throttle-test-first", Start));
    }

    [Fact]
    public void The_same_fault_straight_back_is_not_shown_again()
    {
        const string Text = "throttle-test-repeat";

        Assert.True(App.TryBeginFaultDialog(Text, Start));

        // This is the whole point. A 1.5 second timer faulting means a box every
        // 1.5 seconds, and the user cannot get on with anything while it does.
        Assert.False(App.TryBeginFaultDialog(Text, Start.AddSeconds(1)));
        Assert.False(App.TryBeginFaultDialog(Text, Start.AddSeconds(2)));
        Assert.False(App.TryBeginFaultDialog(Text, Start.AddSeconds(29)));
    }

    [Fact]
    public void The_same_fault_is_shown_again_once_it_has_been_quiet_for_a_while()
    {
        const string Text = "throttle-test-expiry";

        Assert.True(App.TryBeginFaultDialog(Text, Start));
        Assert.False(App.TryBeginFaultDialog(Text, Start.AddSeconds(10)));

        // Long enough to have read and copied the first one.
        Assert.True(App.TryBeginFaultDialog(Text, Start.AddSeconds(45)));
    }

    [Fact]
    public void A_different_fault_is_never_swallowed_by_the_last_ones_cooldown()
    {
        Assert.True(App.TryBeginFaultDialog("throttle-test-alpha", Start));

        // Immediately after a different fault is already showing, a genuinely new
        // one must not be suppressed. Only a repeat of the same text waits.
        Assert.True(App.TryBeginFaultDialog("throttle-test-beta", Start.AddSeconds(1)));
    }

    [Fact]
    public void The_clock_going_backwards_does_not_wedge_the_gate()
    {
        const string Text = "throttle-test-clock";

        Assert.True(App.TryBeginFaultDialog(Text, Start));

        // A negative interval would satisfy any "less than thirty seconds" test
        // either way; this pins that the suppression is on the repeat, not on the
        // arithmetic.
        Assert.False(App.TryBeginFaultDialog(Text, Start.AddSeconds(-5)));
    }
}
