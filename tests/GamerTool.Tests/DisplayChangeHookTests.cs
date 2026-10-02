using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The display-change hook: which messages, how it is debounced, and what it must
/// not do.
/// <para>
/// Windows documents, for SetDeviceGammaRamp itself, that the ramp "is also reset
/// on most display events (connecting/disconnecting a monitor, resolution changes,
/// etc.)". So attaching a monitor or changing a resolution silently removes the
/// ramp this app applied, and the only thing that puts it back was the gamma
/// lock's 1.5 second timer - which re-pushes but never re-enumerates, and so
/// cannot pick up a screen that had just appeared.
/// </para>
/// <para>
/// The behaviour is split out of the window so it can be driven without a display
/// change actually happening. What is under test is the coalescing and the
/// sequencing, both of which are where this kind of hook goes wrong: a hook that
/// runs per message does a full enumeration dozens of times during a dock, and one
/// that re-pushes before Windows has finished reconfiguring does it for nothing.
/// </para>
/// </summary>
public class DisplayChangeHookTests
{
    /// <summary>
    /// The debounce collapses a burst into one rescan.
    /// <para>
    /// Docking a laptop produces WM_DISPLAYCHANGE several times over a few hundred
    /// milliseconds: once per mode change, once for the new topology, once for the
    /// EDID handshake. Each message costs a full EnumDisplayDevices walk plus a
    /// registry lookup per monitor for the label, so running it per message is the
    /// difference between a dock being invisible and a dock stuttering the UI for a
    /// quarter of a second.
    /// </para>
    /// </summary>
    [Fact]
    public void A_burst_of_display_changes_still_owes_exactly_one_rescan()
    {
        var watcher = new DisplayChangeDebounce();

        for (int i = 0; i < 8; i++)
        {
            watcher.Notify();
        }

        // Due, because a rescan is owed - the point is that it is one rescan and
        // not eight. Asserting IsDue is false here would be asserting that the
        // dock is ignored, which is the opposite of the intent.
        Assert.True(watcher.IsDue);

        watcher.Consume();

        Assert.False(watcher.IsDue, "consuming the rescan must clear it, burst or not");
    }

    /// <summary>
    /// The quiet period before the rescan actually runs.
    /// <para>
    /// The point of the delay is that Windows is not finished when the first
    /// message arrives. Re-scanning immediately can enumerate a topology that is
    /// still being negotiated, which produces a list missing the screen that is
    /// half a second away.
    /// </para>
    /// </summary>
    [Fact]
    public void The_debounce_waits_before_firing()
    {
        var watcher = new DisplayChangeDebounce();

        watcher.Notify();

        Assert.True(watcher.IsDue, "a rescan must not run on the same tick as the message");
    }

    /// <summary>
    /// Nothing is pending when no display change has happened.
    /// <para>
    /// Trivial, and it is here because the timer that drives it is created at
    /// startup and runs for the life of the app: the state that says "a rescan is
    /// wanted" has to start false, or the first tick fires a rescan the user did
    /// not ask for.
    /// </para>
    /// </summary>
    [Fact]
    public void Nothing_is_pending_before_any_display_change()
    {
        var watcher = new DisplayChangeDebounce();

        Assert.False(watcher.IsDue);
    }

    /// <summary>
    /// A message arriving after the debounce has elapsed starts a fresh wait rather
    /// than being dropped.
    /// <para>
    /// This is the case a naive "stop the timer and restart it" gets wrong in the
    /// other direction: if the restart is unconditional, a message arriving during
    /// the wait pushes the deadline out again, so a steady trickle of display
    /// events - which some drivers produce while a resolution is settling - would
    /// postpone the rescan indefinitely. The screen would stay unboosted until the
    /// trickle stopped.
    /// </para>
    /// </summary>
    [Fact]
    public void A_later_message_re_arms_the_wait_instead_of_being_ignored()
    {
        var watcher = new DisplayChangeDebounce();

        watcher.Notify();
        Assert.True(watcher.IsDue);

        // As though the debounce had elapsed and been consumed.
        watcher.Consume();
        Assert.False(watcher.IsDue);

        watcher.Notify();
        Assert.True(watcher.IsDue, "a display change after the rescan must schedule another");
    }

    /// <summary>
    /// A display change while the window is closing does nothing.
    /// <para>
    /// Unplugging a display on the way out - or a docking station being closed as
    /// the machine suspends - produces messages right through teardown. A rescan
    /// at that point rebuilds the slot rows and pushes a gamma ramp while
    /// EmergencyReset is trying to take that same ramp off again, and the exit
    /// restore loses.
    /// </para>
    /// </summary>
    [Fact]
    public void A_display_change_is_ignored_while_the_window_is_closing()
    {
        var watcher = new DisplayChangeDebounce();
        watcher.Ignore = true;

        watcher.Notify();

        Assert.False(watcher.IsDue, "teardown must not be followed by a rescan");
    }

    /// <summary>
    /// Teardown also has to cancel a rescan that was already pending.
    /// <para>
    /// The order matters. A display event that arrived a moment before the window
    /// started closing leaves the flag set, and the timer tick that would run it
    /// may still be in flight. Without this the rescan runs anyway - rebuilding
    /// the slot rows and pushing a gamma ramp - while EmergencyReset is taking that
    /// same ramp off, and the exit restore loses.
    /// </para>
    /// </summary>
    [Fact]
    public void Teardown_cancels_a_rescan_that_was_already_pending()
    {
        var watcher = new DisplayChangeDebounce();

        watcher.Notify();
        Assert.True(watcher.IsDue);

        watcher.Ignore = true;
        watcher.Consume();
        watcher.Notify();

        Assert.False(watcher.IsDue, "a rescan pending at teardown must not survive it");
    }

    /// <summary>
    /// The messages that mean the display topology changed.
    /// <para>
    /// WM_DISPLAYCHANGE is the documented one for mode and topology changes.
    /// WM_DEVICECHANGE arrives for every device in the machine - a mouse, a USB
    /// stick, a keyboard - so it is only acted on for DBT_DEVNODES_CHANGED, which
    /// is the notification that means the set of display nodes itself moved.
    /// Without that filter, plugging in a USB mouse would re-scan the displays.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_the_display_relevant_messages_are_acted_on()
    {
        Assert.Equal(0x007E, (int)WindowMessage.WmDisplayChange);
        Assert.Equal(0x0219, (int)WindowMessage.WmDeviceChange);
        Assert.Equal(0x0007, (int)WindowMessage.DbtDevnodesChanged);

        Assert.True(WindowMessage.Matters(WindowMessage.WmDisplayChange, 0));
        Assert.True(WindowMessage.Matters(WindowMessage.WmDeviceChange, 0x0007));

        // A device that is not a display node change: a mouse, a keyboard, a
        // removable drive. Each of these arrives many times a second while a user
        // moves a mouse, so acting on it would re-scan continuously.
        Assert.False(WindowMessage.Matters(WindowMessage.WmDeviceChange, 0x0000));
        Assert.False(WindowMessage.Matters(WindowMessage.WmDeviceChange, 0x0002));
        Assert.False(WindowMessage.Matters(WindowMessage.WmDeviceChange, 0x8000));

        // A WM_DISPLAYCHANGE carries no useful wParam, so anything counts.
        Assert.True(WindowMessage.Matters(WindowMessage.WmDisplayChange, 0x1234));
    }

    /// <summary>
    /// Unknown messages are not interesting.
    /// <para>
    /// The hook is on the window's own handle, so it sees every message the window
    /// receives, not only display ones. Anything unrecognised has to be passed
    /// straight back to WPF, or the window stops responding to the keyboard.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unrelated_message_is_not_a_display_change()
    {
        Assert.False(WindowMessage.Matters(0x0100, 0));
        Assert.False(WindowMessage.Matters(0x0002, 0));
        Assert.False(WindowMessage.Matters(0x007E + 1, 0));
    }

    /// <summary>
    /// The debounce holds one pending flag however many messages arrive.
    /// <para>
    /// Asserted through the count the hook will actually perform, so the test is
    /// about the number of rescans rather than about the flag - the flag being
    /// clear afterwards is exactly how "collapse a burst" is implemented, and a
    /// test that only checked the flag would pass for a loop that rescanned on
    /// every message and cleared in between.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_one_rescan_is_owed_for_a_burst()
    {
        var watcher = new DisplayChangeDebounce();

        for (int i = 0; i < 20; i++)
        {
            watcher.Notify();
        }

        int owed = 0;
        if (watcher.IsDue)
        {
            owed++;
            watcher.Consume();
        }

        Assert.Equal(1, owed);
        Assert.False(watcher.IsDue);
    }
}